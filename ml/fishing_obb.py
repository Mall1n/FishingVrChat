"""Проверяет dataset, обучает OBB-модель и экспортирует выбранный checkpoint в ONNX."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import shutil
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_PROJECT = REPOSITORY_ROOT / "artifacts" / "ml"
IMAGE_EXTENSIONS = {".bmp", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"}
SPLITS = ("train", "validation", "test")


@dataclass
class SplitStatistics:
    """Хранит баланс одного split и источники его samples."""

    images: int = 0
    positives: int = 0
    negatives: int = 0
    sources: dict[str, list[int]] = field(default_factory=lambda: defaultdict(list))


@dataclass
class DatasetAudit:
    """Содержит сводку и диагностические сообщения проверки YOLO OBB dataset."""

    statistics: dict[str, SplitStatistics] = field(default_factory=dict)
    errors: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)

    @property
    def is_valid(self) -> bool:
        return not self.errors


def _image_files(directory: Path) -> dict[str, Path]:
    if not directory.is_dir():
        return {}

    result: dict[str, Path] = {}
    for path in sorted(directory.iterdir()):
        if not path.is_file() or path.suffix.lower() not in IMAGE_EXTENSIONS:
            continue
        if path.stem in result:
            raise ValueError(f"Повторяющийся image stem: {path.stem}")
        result[path.stem] = path
    return result


def _polygon_area(coordinates: list[float]) -> float:
    points = list(zip(coordinates[0::2], coordinates[1::2], strict=True))
    return abs(
        sum(
            x1 * y2 - x2 * y1
            for (x1, y1), (x2, y2) in zip(points, points[1:] + points[:1], strict=True)
        )
    ) / 2


def _validate_label(label_path: Path, split: str, audit: DatasetAudit) -> bool:
    text = label_path.read_text(encoding="utf-8").strip()
    if not text:
        return False

    lines = [line.strip() for line in text.splitlines() if line.strip()]
    if len(lines) != 1:
        audit.errors.append(
            f"{split}/{label_path.name}: ожидается одна OBB, найдено строк: {len(lines)}."
        )
        return True

    tokens = lines[0].split()
    if len(tokens) != 9:
        audit.errors.append(
            f"{split}/{label_path.name}: ожидается class + 8 координат, найдено значений: {len(tokens)}."
        )
        return True

    if tokens[0] != "0":
        audit.errors.append(f"{split}/{label_path.name}: поддерживается только class 0.")

    try:
        coordinates = [float(value) for value in tokens[1:]]
    except ValueError:
        audit.errors.append(f"{split}/{label_path.name}: координаты содержат нечисловое значение.")
        return True

    if any(not math.isfinite(value) or value < 0 or value > 1 for value in coordinates):
        audit.errors.append(
            f"{split}/{label_path.name}: нормализованные координаты должны находиться в диапазоне 0..1."
        )
    if _polygon_area(coordinates) < 1e-6:
        audit.errors.append(f"{split}/{label_path.name}: площадь OBB слишком мала или равна нулю.")
    return True


def _read_source(metadata_path: Path, split: str, audit: DatasetAudit) -> tuple[str | None, int | None]:
    try:
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        audit.warnings.append(f"{split}/{metadata_path.name}: metadata не прочитана ({error}).")
        return None, None

    source_path = metadata.get("sourcePath")
    frame_index = metadata.get("frameIndex")
    source = str(Path(source_path).name) if source_path else None
    return source, frame_index if isinstance(frame_index, int) else None


def audit_dataset(dataset_root: Path) -> DatasetAudit:
    """Проверяет пары image/label, OBB-координаты, split и точные дубликаты кадров."""

    root = dataset_root.expanduser().resolve()
    audit = DatasetAudit()
    stems_by_split: dict[str, set[str]] = {}
    hashes: dict[str, tuple[str, str]] = {}
    source_splits: dict[str, set[str]] = defaultdict(set)

    if not root.is_dir():
        audit.errors.append(f"Dataset не найден: {root}")
        return audit

    for split in SPLITS:
        image_directory = root / "images" / split
        label_directory = root / "labels" / split
        metadata_directory = root / "metadata" / split
        statistics = SplitStatistics()
        audit.statistics[split] = statistics

        try:
            images = _image_files(image_directory)
        except ValueError as error:
            audit.errors.append(f"{split}: {error}")
            images = {}
        labels = {path.stem: path for path in label_directory.glob("*.txt")} if label_directory.is_dir() else {}
        metadata = (
            {path.stem: path for path in metadata_directory.glob("*.json")}
            if metadata_directory.is_dir()
            else {}
        )
        stems_by_split[split] = set(images)
        statistics.images = len(images)

        for missing_stem in sorted(set(images) - set(labels)):
            audit.errors.append(f"{split}/{missing_stem}: отсутствует label.")
        for orphan_stem in sorted(set(labels) - set(images)):
            audit.errors.append(f"{split}/{orphan_stem}: label не имеет соответствующего image.")
        for missing_stem in sorted(set(images) - set(metadata)):
            audit.warnings.append(f"{split}/{missing_stem}: отсутствует audit metadata.")

        for stem, image_path in images.items():
            label_path = labels.get(stem)
            if label_path is not None and _validate_label(label_path, split, audit):
                statistics.positives += 1
            elif label_path is not None:
                statistics.negatives += 1

            image_hash = hashlib.sha256(image_path.read_bytes()).hexdigest()
            if image_hash in hashes:
                previous_split, previous_stem = hashes[image_hash]
                if previous_split != split:
                    audit.errors.append(
                        f"Точный дубликат кадра между split: {previous_split}/{previous_stem} и {split}/{stem}."
                    )
            else:
                hashes[image_hash] = (split, stem)

            metadata_path = metadata.get(stem)
            if metadata_path is not None:
                source, frame_index = _read_source(metadata_path, split, audit)
                if source:
                    source_splits[source].add(split)
                    if frame_index is not None:
                        statistics.sources[source].append(frame_index)

    # Одинаковый sample id в разных split всегда означает прямое пересечение одной разметки.
    for index, left_split in enumerate(SPLITS):
        for right_split in SPLITS[index + 1 :]:
            for stem in sorted(stems_by_split[left_split] & stems_by_split[right_split]):
                audit.errors.append(
                    f"Sample присутствует в двух split: {left_split}/{stem} и {right_split}/{stem}."
                )

    for source, source_split_set in sorted(source_splits.items()):
        if len(source_split_set) > 1:
            audit.warnings.append(
                f"Источник {source} встречается в split: {', '.join(sorted(source_split_set))}. "
                "Это допустимо только для разных, не пересекающихся эпизодов рыбалки."
            )

    train = audit.statistics["train"]
    validation = audit.statistics["validation"]
    if train.images == 0 or train.positives == 0:
        audit.errors.append("Train должен содержать positive samples.")
    if validation.images == 0 or validation.positives == 0:
        audit.errors.append("Validation должен содержать positive samples.")
    if audit.statistics["test"].images == 0:
        audit.warnings.append("Test пуст: финальная независимая оценка будет недоступна.")

    return audit


def print_audit(audit: DatasetAudit) -> None:
    print("\nDataset summary")
    print("split       images  positive  negative")
    for split in SPLITS:
        statistics = audit.statistics.get(split, SplitStatistics())
        print(
            f"{split:<12}{statistics.images:>6}{statistics.positives:>10}{statistics.negatives:>10}"
        )

    if audit.warnings:
        print("\nПредупреждения:")
        for warning in audit.warnings:
            print(f"  WARN: {warning}")
    if audit.errors:
        print("\nОшибки:")
        for error in audit.errors:
            print(f"  ERROR: {error}")
    print("\nРезультат: " + ("dataset готов." if audit.is_valid else "dataset содержит ошибки."))


def require_valid_dataset(dataset_root: Path) -> DatasetAudit:
    audit = audit_dataset(dataset_root)
    print_audit(audit)
    if not audit.is_valid:
        raise SystemExit(2)
    return audit


def write_dataset_yaml(dataset_root: Path, destination: Path) -> Path:
    destination.parent.mkdir(parents=True, exist_ok=True)
    root_value = json.dumps(dataset_root.expanduser().resolve().as_posix(), ensure_ascii=False)
    destination.write_text(
        "\n".join(
            (
                f"path: {root_value}",
                "train: images/train",
                "val: images/validation",
                "test: images/test",
                "names:",
                "  0: fishing_panel",
                "",
            )
        ),
        encoding="utf-8",
    )
    return destination.resolve()


def load_ml_dependencies() -> tuple[Any, Any]:
    try:
        import torch
        from ultralytics import YOLO
    except ImportError as error:
        raise SystemExit(
            "ML-зависимости не установлены. Выполни ml/setup.ps1 и повтори команду."
        ) from error
    return torch, YOLO


def resolve_device(torch: Any, requested: str) -> str | int:
    if requested != "auto":
        return requested
    if torch.cuda.is_available():
        print(f"Используется GPU: {torch.cuda.get_device_name(0)}")
        return 0
    print("WARN: CUDA недоступна, будет использован CPU. Обучение займёт значительно больше времени.")
    return "cpu"


def command_check(args: argparse.Namespace) -> None:
    audit = audit_dataset(args.dataset)
    print_audit(audit)
    if not audit.is_valid:
        raise SystemExit(2)


def command_train(args: argparse.Namespace) -> None:
    require_valid_dataset(args.dataset)
    torch, yolo = load_ml_dependencies()
    device = resolve_device(torch, args.device)
    project = args.project.expanduser().resolve()
    data_yaml = write_dataset_yaml(args.dataset, project / "generated" / "fishing-panel.yaml")
    model = yolo(args.model)
    model.train(
        data=str(data_yaml),
        epochs=args.epochs,
        imgsz=args.imgsz,
        batch=args.batch,
        device=device,
        workers=args.workers,
        project=str(project),
        name=args.name,
        seed=args.seed,
        deterministic=True,
        patience=args.patience,
        pretrained=True,
        plots=True,
        amp=True,
        degrees=12.0,
        translate=0.10,
        scale=0.35,
        perspective=0.0005,
        hsv_h=0.02,
        hsv_s=0.45,
        hsv_v=0.40,
        flipud=0.0,
        fliplr=0.0,
        mosaic=0.5,
        close_mosaic=15,
    )


def print_decision_gate_summary(
    model: Any,
    dataset_root: Path,
    split: str,
    imgsz: int,
    device: str | int,
    confidence_threshold: float,
    minimum_aspect_ratio: float,
) -> None:
    """Оценивает итоговое frame-level решение после confidence и geometry gate."""

    positive_total = 0
    negative_total = 0
    detected_positive = 0
    false_positive = 0
    image_directory = dataset_root.expanduser().resolve() / "images" / split
    label_directory = dataset_root.expanduser().resolve() / "labels" / split

    for result in model.predict(
        image_directory,
        imgsz=imgsz,
        conf=min(0.05, confidence_threshold),
        device=device,
        verbose=False,
        stream=True,
    ):
        label_path = label_directory / f"{Path(result.path).stem}.txt"
        is_positive = bool(label_path.read_text(encoding="utf-8").strip())
        accepted = False
        for confidence, box in zip(result.obb.conf.cpu(), result.obb.xywhr.cpu(), strict=True):
            width = float(box[2])
            height = float(box[3])
            aspect_ratio = max(width, height) / max(min(width, height), 1e-9)
            if float(confidence) >= confidence_threshold and aspect_ratio >= minimum_aspect_ratio:
                accepted = True
                break

        if is_positive:
            positive_total += 1
            detected_positive += int(accepted)
        else:
            negative_total += 1
            false_positive += int(accepted)

    print("\nDeployment gate")
    print(f"confidence>={confidence_threshold:.2f}")
    print(f"aspect_ratio>={minimum_aspect_ratio:.2f}")
    print(f"positive_detected={detected_positive}/{positive_total}")
    print(f"negative_false_positive={false_positive}/{negative_total}")


def command_test(args: argparse.Namespace) -> None:
    require_valid_dataset(args.dataset)
    if not args.weights.is_file():
        raise SystemExit(f"Checkpoint не найден: {args.weights}")
    torch, yolo = load_ml_dependencies()
    device = resolve_device(torch, args.device)
    project = args.project.expanduser().resolve()
    data_yaml = write_dataset_yaml(args.dataset, project / "generated" / "fishing-panel.yaml")
    model = yolo(str(args.weights.resolve()))
    metrics = model.val(
        data=str(data_yaml),
        split="test",
        imgsz=args.imgsz,
        batch=args.batch,
        conf=args.conf,
        device=device,
        workers=args.workers,
        project=str(project),
        name=args.name,
        plots=True,
    )
    box = metrics.box
    print("\nTest metrics")
    print(f"precision={float(box.mp):.4f}")
    print(f"recall={float(box.mr):.4f}")
    print(f"mAP50={float(box.map50):.4f}")
    print(f"mAP50-95={float(box.map):.4f}")
    print_decision_gate_summary(
        model,
        args.dataset,
        "test",
        args.imgsz,
        device,
        args.decision_conf,
        args.min_aspect_ratio,
    )


def command_export(args: argparse.Namespace) -> None:
    if not args.weights.is_file():
        raise SystemExit(f"Checkpoint не найден: {args.weights}")
    torch, yolo = load_ml_dependencies()
    device = resolve_device(torch, args.device)
    exported = Path(
        yolo(str(args.weights.resolve())).export(
            format="onnx",
            imgsz=args.imgsz,
            batch=1,
            dynamic=False,
            simplify=False,
            opset=17,
            device=device,
        )
    ).resolve()
    if args.output is not None:
        output = args.output.expanduser().resolve()
        output.parent.mkdir(parents=True, exist_ok=True)
        if output != exported:
            shutil.copy2(exported, output)
        exported = output
    print(f"ONNX сохранена: {exported}")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)

    check = commands.add_parser("check", help="Проверить структуру и labels без запуска ML.")
    check.add_argument("--dataset", type=Path, required=True)
    check.set_defaults(handler=command_check)

    train = commands.add_parser("train", help="Обучить OBB-модель на Train/Validation.")
    train.add_argument("--dataset", type=Path, required=True)
    train.add_argument("--model", default="yolo26n-obb.pt")
    train.add_argument("--epochs", type=int, default=150)
    train.add_argument("--imgsz", type=int, default=1024)
    train.add_argument("--batch", type=int, default=-1, help="-1 автоматически использует около 60%% VRAM.")
    train.add_argument("--workers", type=int, default=4)
    train.add_argument("--patience", type=int, default=35)
    train.add_argument("--seed", type=int, default=42)
    train.add_argument("--device", default="auto", help="auto, cpu либо номер GPU, например 0.")
    train.add_argument("--project", type=Path, default=DEFAULT_PROJECT)
    train.add_argument("--name", default="fishing-panel-obb")
    train.set_defaults(handler=command_train)

    test = commands.add_parser("test", help="Один раз оценить выбранный checkpoint на Test.")
    test.add_argument("--dataset", type=Path, required=True)
    test.add_argument("--weights", type=Path, required=True)
    test.add_argument("--imgsz", type=int, default=1024)
    test.add_argument("--batch", type=int, default=8)
    test.add_argument("--conf", type=float, default=0.001)
    test.add_argument("--decision-conf", type=float, default=0.50)
    test.add_argument("--min-aspect-ratio", type=float, default=10.0)
    test.add_argument("--workers", type=int, default=4)
    test.add_argument("--device", default="auto")
    test.add_argument("--project", type=Path, default=DEFAULT_PROJECT)
    test.add_argument("--name", default="test")
    test.set_defaults(handler=command_test)

    export = commands.add_parser("export", help="Экспортировать checkpoint в статическую ONNX.")
    export.add_argument("--weights", type=Path, required=True)
    export.add_argument("--output", type=Path)
    export.add_argument("--imgsz", type=int, default=1024)
    export.add_argument("--device", default="auto")
    export.set_defaults(handler=command_export)
    return parser


def main() -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    if sys.version_info < (3, 10):
        raise SystemExit("Требуется Python 3.10 или новее.")
    args = build_parser().parse_args()
    args.handler(args)


if __name__ == "__main__":
    main()

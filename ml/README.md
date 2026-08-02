# Обучение OBB-модели

Pipeline использует transfer learning `yolo26n-obb.pt`. Модель учится находить только рамку мини-игры (`class 0: fishing_panel`). Negative samples задаются пустыми label-файлами.

Результаты первого завершённого обучения находятся в [BASELINE_RESULTS.md](BASELINE_RESULTS.md).

## 1. Установка Python и окружения

Требуется Python 3.10–3.14. Рекомендуемая версия для проекта — Python 3.12. Если команда `python --version` не показывает полноценную версию, установи Python и заново открой терминал:

```powershell
winget install --exact --id Python.Python.3.12
```

Из корня репозитория создай `.venv` и установи CUDA-сборку PyTorch, Ultralytics и ONNX:

```powershell
powershell -ExecutionPolicy Bypass -File ml/setup.ps1
```

По умолчанию используется PyTorch для CUDA 12.8. Скрипт в конце должен вывести `CUDA available: True` и название NVIDIA GPU.

## 2. Проверка dataset

Проверка не загружает модель и не использует GPU:

```powershell
.\.venv\Scripts\python.exe ml\fishing_obb.py check `
  --dataset "C:\Users\savva\Downloads\model dataset 0.1"
```

Команда проверяет пары PNG/label, формат восьми OBB-координат, диапазон `0..1`, площадь рамки, прямые дубликаты между split и наличие Train/Validation. Один исходный файл в нескольких split выводится как warning: это допустимо только тогда, когда внутри файла находятся разные рыбалки, разделённые по времени и сцене.

## 3. Первое обучение

```powershell
.\.venv\Scripts\python.exe ml\fishing_obb.py train `
  --dataset "C:\Users\savva\Downloads\model dataset 0.1"
```

Начальные параметры рассчитаны на RTX 3080 Ti:

- `yolo26n-obb.pt` с pretrained weights;
- размер входа `1024`, чтобы не потерять узкую рамку при уменьшении кадра;
- до 150 epochs с early stopping после 35 epochs без улучшения;
- автоматический batch примерно на 60% VRAM;
- умеренные геометрические и цветовые augmentation без зеркальных отражений.

Результаты сохраняются в `artifacts/ml/fishing-panel-obb*`. Главный файл — `weights/best.pt`. Ultralytics также сохраняет графики метрик, confusion matrix и изображения Validation с предсказаниями.

## 4. Test

Test не участвует в выборе checkpoint или threshold. Команду следует выполнить один раз после просмотра результатов Validation и выбора `best.pt`:

```powershell
.\.venv\Scripts\python.exe ml\fishing_obb.py test `
  --dataset "C:\Users\savva\Downloads\model dataset 0.1" `
  --weights "artifacts\ml\fishing-panel-obb\weights\best.pt"
```

Помимо raw OBB-метрик команда проверяет зафиксированный deployment gate: `confidence ≥ 0.50` и отношение длинной стороны OBB к короткой `≥ 10`. Эти значения выбраны на Validation до просмотра Test. Gate отсекает широкие вертикальные символы и UI-элементы, которые нейросеть может принять за узкую панель.

## 5. Экспорт ONNX

```powershell
.\.venv\Scripts\python.exe ml\fishing_obb.py export `
  --weights "artifacts\ml\fishing-panel-obb\weights\best.pt" `
  --output "artifacts\models\fishing-panel-obb.onnx"
```

Экспорт использует фиксированный вход `1 × 3 × 1024 × 1024`, FP32 и ONNX opset 17. Это упрощает первый ONNX Runtime inference в .NET. Оптимизацию размера и FP16 следует делать только после сравнения точности и latency базового варианта.

## 6. Проверка модели в приложении

В правом блоке `ONNX detector` выбери экспортированный файл и включи detector. Приложение выполняет тот же letterbox `1024 × 1024`, читает output в формате `[x, y, w, h, confidence, class, angle]` и применяет deployment gate из раздела Test. Первая проверка выполняется через CPU backend; GPU backend подключается отдельно после сравнения точности и latency.

Первый analyzed frame включает создание GPU pipeline и не характеризует рабочую latency. Прогони несколько кадров либо небольшой фрагмент видео и сравни `median` и `p95`. Все ошибки на новых видео сначала сохраняй как отдельный материал для следующего Train; уже использованный Test не применяй для подбора новых thresholds.

# OBB baseline 2026-08-01

## Dataset

Первый зафиксированный dataset:

| Split | Positive | Negative | Всего |
|---|---:|---:|---:|
| Train | 177 | 149 | 326 |
| Validation | 22 | 19 | 41 |
| Test | 20 | 20 | 40 |

`2-05.mp4` и `2-06.mp4` содержат по две разные рыбалки. Их непересекающиеся временные участки распределены между Validation и Test намеренно.

## Обучение

- pretrained model: `yolo26n-obb.pt`;
- вход: `1024 × 1024`;
- GPU: NVIDIA GeForce RTX 3080 Ti;
- обучение остановлено early stopping на epoch 93;
- лучший checkpoint получен на epoch 58;
- время обучения: 0,277 часа;
- параметры модели: 2 446 602.

Validation для `best.pt`:

| Метрика | Значение |
|---|---:|
| Precision | 1,000 |
| Recall | 0,998 |
| mAP50 | 0,995 |
| mAP50-95 | 0,898 |

PyTorch inference на RTX 3080 Ti во время Validation занимал около `3,6 мс` на изображение без учёта полного C# pipeline.

## Deployment gate

На Validation до просмотра Test зафиксировано правило принятия OBB:

- confidence не ниже `0,50`;
- отношение длинной стороны OBB к короткой не ниже `10,0`.

Настоящие Validation panels имели aspect ratio `12,57–14,35`, false positives — `3,33–6,38`. Gate обнаружил `22/22` positive и отклонил все `19/19` negative samples. В частности, он отсекает большой белый знак `!`, который raw-модель находила с confidence `0,681`.

## Финальный Test

Test запущен после фиксации checkpoint, confidence и geometry gate:

| Метрика | Значение |
|---|---:|
| Precision | 0,9928 |
| Recall | 0,9500 |
| mAP50 | 0,9926 |
| mAP50-95 | 0,8395 |

Итоговый gate:

- positive detected: `18/20`;
- negative false positive: `0/20`.

Этот Test считается использованным и не должен применяться для настройки текущей версии. Если обнаруженные Test-ошибки станут источником нового Train, для следующей независимой оценки потребуется новый Test dataset.

## ONNX

- файл: `artifacts/models/fishing-panel-obb.onnx`;
- вход: `images [1, 3, 1024, 1024]`;
- выход: `output0 [1, 300, 7]`;
- opset: 17;
- SHA-256: `86c597ee8ffc39a883f1f85de50041158800ff7c395a0a57aa89363c5d106f12`.

ONNX Runtime проверен на всех 41 Validation samples. Несмотря на различия отдельных raw confidence, deployment gate полностью совпал с PyTorch. На Test ONNX также получил `18/20` positive и `0/20` false positives.

Checkpoint, ONNX и отчёты находятся в игнорируемой Git папке `artifacts/`. Для восстановления baseline необходимо отдельно сохранить `weights/best.pt` и `artifacts/models/fishing-panel-obb.onnx` либо повторить обучение с зафиксированными параметрами.

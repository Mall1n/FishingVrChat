# Fishing Vision Assistant

Диагностический Windows-анализатор мини-игры FISH! в VRChat. Приложение проектируется как визуальный помощник: оно анализирует изображение и показывает рекомендацию `ДЕРЖАТЬ`, `ОТПУСТИТЬ` или `НЕ УВЕРЕН`, но не отправляет ввод в игру.

Архитектурный план находится в [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).
Инструкции по обучению oriented detector находятся в [ml/README.md](ml/README.md).
Метрики первого завершённого baseline находятся в [ml/BASELINE_RESULTS.md](ml/BASELINE_RESULTS.md).

## Требования

- Windows 10 или Windows 11.
- .NET 8 SDK.
- Visual Studio 2022 либо VS Code с расширением C# Dev Kit.

## Структура solution

- `FishingVisionAssistant.App` — WPF-интерфейс и композиция приложения.
- `FishingVisionAssistant.Core` — модели detector, tracking и безопасный `AdviceController`.
- `FishingVisionAssistant.Capture` — контракты offline-источников и live capture.

`Core` и `Capture` не зависят друг от друга. `App` объединяет их на уровне пользовательского сценария.

## Запуск через VS Code

1. Открыть папку `C:\Projects\FishingVrChat`.
2. Установить расширение C# Dev Kit, если оно ещё не установлено.
3. Нажать `F5` и выбрать конфигурацию `Fishing Vision Assistant`.

Также приложение можно запустить из встроенного терминала:

```powershell
dotnet run --project src/FishingVisionAssistant.App/FishingVisionAssistant.App.csproj
```

Сборка всего solution:

```powershell
dotnet build FishingVrChat.sln
```

## Текущее состояние

Реализовано:

- диагностическое окно и загрузку статического изображения;
- покадровый Frame Inspector для локальных видео с play/pause, timeline, seek и скоростью воспроизведения;
- настраиваемый HSV Lab и legacy `PanelDetector` по цвету, белой области и геометрии;
- overlay найденной рамки, preview маски и нормализацию `96 × 640` через perspective transform;
- интерактивную OBB-разметку четырьмя точками с выпрямленным preview выбранной области;
- сохранение чистого PNG, YOLO OBB label и audit metadata в `Train`, `Validation` или `Test`;
- восстановление, исправление, перезапись и удаление ранее сохранённой разметки текущего кадра;
- автоматическое устранение копии одного sample в другом split при переносе;
- сохранение последнего видео, позиции, dataset, split, HSV и состояния режима разметки между запусками;
- LRU-кэш последних результатов и показатели cold start, median и p95;
- CLI для проверки OBB dataset, обучения, независимой оценки Test и экспорта ONNX;
- контракты кадров и начальный безопасный controller.

Горячие клавиши Frame Inspector: `Space` — play/pause, `←`/`→` — одна секунда, `Shift+←`/`Shift+→` — один кадр.

Горячие клавиши OBB-разметки:

- `Enter` — сохранить предложенную или исправленную OBB;
- `E` — исправить четыре существующие точки;
- `M` — поставить четыре точки вручную;
- `N` — сохранить кадр как negative без рамки;
- `Delete` — после подтверждения удалить PNG, label и metadata текущего sample;
- `S` — пропустить кадр без сохранения.

После сохранения приложение остаётся на текущем кадре. Голубая OBB означает предложение legacy detector, зелёная — загруженный ground truth, оранжевая — редактирование.

## Подготовка OBB dataset

Для всех записей используется одна корневая папка dataset. Приложение создаёт структуру автоматически:

```text
dataset/
├─ images/{train,validation,test}/
├─ labels/{train,validation,test}/
└─ metadata/{train,validation,test}/
```

Все кадры одной непрерывной ловли необходимо сохранять в одном split. Разные ловли внутри одного длинного видео можно разделить только при отсутствии пересечения по времени и при заметно отличающейся сцене; для финального Test всё равно предпочтительнее отдельная запись. Соседние кадры одной ловли нельзя распределять между Train и Validation/Test: это создаёт data leakage и завышает качество проверки.

Рекомендуемый первый набор — 150–300 разнообразных размеченных кадров. В него должны входить positive OBB, кадры без мини-игры и hard negatives, на которых legacy detector принимает удочку или фон за рамку. Validation и Test формируются из целых отдельных видео.

Текущий классический detector недостаточно устойчив на разных биомах и освещении, поэтому следующий этап — обучение небольшого oriented object detector, экспорт лучшего checkpoint в ONNX и подключение inference к приложению. Поиск белой зоны и значка рыбы будет выполняться обычным OpenCV внутри найденной и выпрямленной OBB. Tracking и live capture подключаются после проверки offline-модели.

Подготовленный ML pipeline описан в `ml/README.md`. Он использует Train/Validation во время обучения, запускает Test отдельной явной командой и сохраняет checkpoints и отчёты в игнорируемую Git папку `artifacts/`.

Первый `yolo26n-obb` baseline обучен и экспортирован в ONNX. Зафиксированный deployment gate использует confidence `0,50` и минимальный aspect ratio `10,0`; подробные Validation/Test результаты и hash ONNX записаны в `ml/BASELINE_RESULTS.md`.

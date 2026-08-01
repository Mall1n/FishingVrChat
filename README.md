# Fishing Vision Assistant

Диагностический Windows-анализатор мини-игры FISH! в VRChat. Приложение проектируется как визуальный помощник: оно анализирует изображение и показывает рекомендацию `ДЕРЖАТЬ`, `ОТПУСТИТЬ` или `НЕ УВЕРЕН`, но не отправляет ввод в игру.

Архитектурный план находится в [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md).

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

1. Открыть папку `C:\LLM\FishingVrChat`.
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

Каркас содержит:

- диагностическое окно и загрузку статического изображения;
- первый OpenCV `PanelDetector` по HSV-маске и геометрии;
- overlay найденной рамки, preview маски и нормализацию `96 × 640` через perspective transform;
- контракты кадров и начальный безопасный controller.

Обработка видео, поиск белой зоны и значка рыбы, tracking и live capture будут подключаться следующими этапами.

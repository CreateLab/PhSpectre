# 05. План реализации (после согласования дизайна)

Каждый шаг собирается, запускается и тестируется отдельно. Приложение между шагами остаётся рабочим. `ExportMode` и рендеры (`PaletteImageRenderer`, `RecipeCardRenderer`, `CollageComposer`) **не меняем**: меняется только то, как пользователь их выбирает.

Обозначения: ✂ — что ломаем сознательно; 🧪 — как проверяем.

## Шаг 1 · Токены и общие компоненты
- `Styles/Colors.axaml`: новые значения `text-muted`, `success/warning/error` (Light), `border-strong`, `canvas` (одинаков в обеих темах); остальные ключи сохраняем.
- `Styles/Tokens.axaml`: размеры шрифта `Caption…Display`, `film.w/h`, `TouchTarget` по платформе (48 Android / 32 desktop), `Breakpoint 840`.
- `Styles/Controls.axaml`: один `Surface.card`; стили `LookTile`, `TabStrip`, `Chip`, `Toast`, `Accordion`.
- Шрифт: **Inter** для UI, JetBrains Mono для чисел; Space Grotesk убрать (решение принято).
- 🧪 запуск Desktop, обе темы, ничего не сломано; тесты ядра.

## Шаг 2 · Каркас: режимы, лента, единый Export
- Режимы **Edit · Collage · Camera**, **Edit по умолчанию при запуске**. Индексы `TopModeIndex`: 0 Edit, 1 Collage, 2 Camera.
- Верхняя панель: логотип · режимы · Undo/Redo · «⋯» (Appearance, Updates, About) · **Export**.
- Одна лента `Filmstrip` (десктоп и мобайл), «Open» слева.
- Единый `ExportBar` вместо трёх футеров; одна кнопка Export + Ctrl+S.
- ✂ Режим Frame (Card | Recipe) как пункт верхней панели, меню Export, «Export settings…», отдельный `BatchErrorsWindow` (→ тост).
- Файлы: `MainWindow.axaml`, `MainView.axaml`, `MainWindowViewModel.cs`, `MainViewModel.cs`, `SettingsViewModel.cs`, `BatchErrorsWindow.axaml`.
- 🧪 открыть папку → выбрать фото → Export; Save all; мобайл: шторка.

## Шаг 3 · Edit — главный экран (Look + Export)
- `ProcessView` → `EditView`: tab strip **Look · Frame · Export**.
- Look: один Amount на выбранный Look (а не два независимых слайдера), строка слоёв, поиск + фильтры, **сетка миниатюр** на фото пользователя (фон-рендер, ленивый, с кэшем), состояния пусто/загрузка/ошибка.
- Export: формат, качество, размер, папка, Export all.
- Горячие клавиши (Z/Y, `\`, 1–3, G).
- Остаются `ProcessViewModel`, `GradeLibrary`, `GradeStack`, `PhotoFrame`; добавляются `LookTileViewModel`, `ThumbnailRenderer`.
- ✂ Сегодняшний длинный инспектор «Layers + Frame + Library».
- 🧪 юнит-тесты `ThumbnailRenderer` (ключ кэша, размер); UI-прогон: импорт → выбор → Amount → Before → Export.

## Шаг 4 · Единый конвейер Edit → Frame
- Общий `EditState` на фото (Look, параметры рамки); Frame (Border/Card/Recipe card) рисуется из обработанного фото.
- Предпросмотр Frame использует тот же `GradeStack`.
- ✂ Старые Card/Recipe, читающие оригинал напрямую.
- 🧪 фото с Look → Card: цвета карточки согласованы; без Look результат не меняется (существующие тесты рендера).

## Шаг 5 · Frame внутри Edit (самый большой шаг)
- Вкладка **Frame**: выбор типа None · Border · Card · Recipe card; под ним только настройки этого типа.
- Border: плитки + Customize (уже есть `PhotoFrame`, `PhotoFrameStore`).
- Card: плитки Card style; строки Palette / Camera info / QR code с «More»; поля метаданных в «Fields…».
- Recipe card: стиль, поля, Save to library, Send to camera (логику переиспользуем из `SettingsViewModel.Camera.cs`/`RecipeLibraryViewModel`).
- Тип рамки → `ExportMode` (Card/InfoOnly/Recipe), остальное без изменений.
- **Доступность Recipe card:** чистая функция `FrameAvailability.Check(type, hasExifRecipe, lookApplied)` в ядре (`PhSpectre`), использует результат `RecipeReader.Read` и состояние `GradeStack` (`IsEmpty`). UI: неактивная плитка + строка-причина; автоматический откат на Card с тостом и Undo при наложении Look или смене фото; в «Export all» фото без рецепта обрабатываются как сейчас (сообщение), а не молча.
- 🧪 юнит-тесты `FrameAvailability` (нет рецепта, есть рецепт без Look, есть Look с рецептом, пресет вместо LUT, выключенный слой Look считается неприменённым — `GradeStack.IsEmpty`).
- Переименования по глоссарию (01); `LabelScaleIndex` как один слайдер.
- ✂ Вкладки Style / Content / Export старого инспектора (≈ 60 контролов), `SettingsPanel.axaml` в нынешнем виде.
- 🧪 все существующие сценарии: карточка, рецепт, QR, метаданные, Save all.

## Шаг 6 · Collage
- Тот же каркас; инспектор Layout · Content · Export; плитки раскладок; чекбоксы на ленте.
- 🧪 Collage generate/regenerate/save.

## Шаг 7 · Camera
- Только стили и общие компоненты (`Surface.card`, кнопки, тосты, состояния); логика, `CameraDialogViewModel`, `CameraService`, PTP **не трогаем**.
- Редактор рецепта: секции Tone / Color / Effects сворачиваются, одна кнопка Save.
- 🧪 регресс по тестам камеры; ручной прогон диалога записи (Warning → Writing → Done / Error).

## Шаг 8 · Адаптивность
- Единый брейкпоинт 840 (`IsWide`); трёхпанельный режим на ≥ 1600; телефон: нижняя панель + шторка одной темы; цели ≥ 48 dp.
- 🧪 окна 360 / 840 / 1440 / 2560 px.

## Шаг 9 · Чистка
- Удалить дубли (флайауты, eye-тогглы, футеры), мёртвые стили, debug-оверлей из релиза; `AutomationProperties.Name` у иконок-кнопок; проверка контраста по таблице.
- 🧪 `dotnet build` всех проектов, `dotnet test`, ручной чеклист платформ.

## Порядок и риски
1 → 2 → 3 дают видимый результат (Edit главный) за три шага; 4 — архитектурный (EditState), после 3; 5 — самый объёмный, делать после 4; 6–7 можно параллелить; 8–9 в конце.
Риски: расчёт миниатюр на CPU (кэш обязателен); два корневых VM (desktop/mobile) — менять синхронно; Android не проверяется без устройства; импорт на Android не работает (нет локального пути); шаг 5 трогает логику выбора `ExportMode`, поэтому покрывать тестами.

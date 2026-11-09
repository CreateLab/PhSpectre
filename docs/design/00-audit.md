# 00. Аудит текущего интерфейса

Состояние на момент переработки: Avalonia 12, `PhSpectre.Avalonia`. Режимы сверху: Frame · Collage · Grade · Camera (Card/Recipe уже слиты в Frame переключателем внутри инспектора). Это документ-основа: любое решение в 01–03 ссылается на пункты отсюда (A1, A2 …).

## Что не так

| # | Проблема | Где |
|---|---|---|
| A1 | **Grade не главный.** Это четвёртый режим, внутри один длинный инспектор: слои, рамка, библиотека, экспорт. Приложение по-прежнему «про карточку с палитрой». | `Views/ProcessView.axaml` |
| A2 | **Инспектор Frame — стена контролов.** Style ≈33, Content ≈28 + 7 полей метаданных, Export ≈9. Десятки условных секций; половина настроек на экране отключена или спрятана хинтом. | `Views/SettingsPanel.axaml`, флаги `ShowXSectionUI` в `SettingsViewModel.cs` |
| A3 | **Дубли контролов.** Save/Export в 4 местах (верхняя панель, меню, футер вкладки, телефонная строка); футер экспорта размножен в 3 разметках; Appearance-флайаут ×2; тоггл-«глаза» рецепта отдельно для десктопа и мобайла; сохранение в редакторе рецепта в шапке и футере. | `MainWindow.axaml`, `MainView.axaml`, `RecipeEditorView.axaml` |
| A4 | **Один контрол — два имени.** `LabelScaleIndex` показан как «Label size» и как «Text size». | `SettingsPanel.axaml` |
| A5 | **Жаргон.** «Grade», «Plate», «Guide lines (Golden, Cross)», «Sampling (Vivid/Standard/Contrast)», «Working quality», «Include note», «Card» = и режим, и тема карточки, «Export settings…» открывает вкладку. | `SettingsPanel.axaml`, меню Export |
| A6 | **Дрейф стилей.** Токены говорят 600/1000, код использует 840; размеры шрифта 9, 10, 15, 17, 22, 24 вне шкалы 11/12/13/14/18; хардкод цветов (`#111113`, `#F3F3F3`, `#CC000000`, `White`); `Border.card` определён в двух местах; `Classes="layer"` без стиля. | `Styles/*.axaml`, `CameraModeView.axaml`, `CameraOverlay.axaml`, `ProcessView.axaml` |
| A7 | **Недоделанные поверхности.** `BatchErrorsWindow` — без темы; debug-оверлей в `MainView`; About спрятан внизу вкладки Export, обновление есть только в десктопной шапке. | `BatchErrorsWindow.axaml`, `MainView.axaml` |
| A8 | **Лента кадров разная.** Десктоп — ListBox 96×62 с заголовком и галочками экспорта; мобайл — кнопки 72×52 «недавние за сессию». Нет единого «папка → кадры → выбор». | `MainWindow.axaml`, `MainView.axaml` |
| A9 | **Камера: глубокая вложенность.** Правая колонка = Grid → ScrollViewer → StackPanel → несколько StackPanel под разными `IsVisible`; на мобайле состояния спрятаны за флагами `Library`. | `CameraModeView.axaml` |
| A10 | **Редактор рецепта: 16 полей одной стеной** + «Discard unsaved changes?» как встроенное подтверждение. | `RecipeEditorView.axaml` |
| A11 | **Grade пока не связан с Frame/Collage.** Карточка и коллаж берут оригинал, а не обработанное фото. | `MainViewModel`/`MainWindowViewModel` (рендер), `ProcessViewModel` |

## Что уже хорошо (сохраняем)
- Нейтральная палитра, две темы с одинаковым набором из 25 ключей (`Colors.axaml`).
- Единый набор контролов V2: `seg`, `chips`, `options`, `switch`, `Slider.v2`, `filmstrip`.
- Иконки Material в одном размере (12–20).
- Состояния камеры продуманы (Warning/Writing/Done/Error, восстановление).
- Адаптивность уже есть: боковая панель на десктопе, шторка на телефоне.

## Список «выкинуть / слить / упростить»

| Кластер | Решение | Причина |
|---|---|---|
| Кнопки Save / Export (4 места) | **Слить в одну** кнопку Export в верхней панели + Ctrl+S; в мобайле — один пункт в нижней панели | A3 |
| Экспортный футер ×3 | **Один компонент** `ExportBar` (имя файла, размер, кнопка) | A3 |
| Вкладка Export (Format/Size/Quality/SaveTo/About) | **Оставить как панель экспорта**, About вынести в диалог «О приложении» (меню), Working quality убрать из верхнего уровня | A3, A7 |
| Style ≈33 → | **Пресеты карточек** (миниатюры: Dark, Light, Film strip, Overlay…) + раздел «More» с остальным | A2 |
| Swatches (Hex, labels, percent, shape, sizes) | **Свернуть под «Palette ▸ More»**, один слайдер размера | A2, A4 |
| Guide lines (5 вариантов) | Переименовать в **«Composition grid»**, перенести в Grade/Frame как оверлей холста (кнопка в тулбаре холста), не в инспектор | A5 |
| Sampling / Sort | Объединить в «Palette ▸ More», дать человеческие подписи («Vivid» → «Punchy colors») | A5 |
| Camera plate (Text/Layout/Metadata 7 полей) | «Camera info»: Off · Short · Detailed (один сегмент) + «Edit fields…» | A2, A5 |
| QR (6 полей) | Одна строка «QR code» с переключателем; настройки — в раскрывающейся секции | A2 |
| Тоггл-глаза рецепта ×2 | **Один компонент** `FieldToggleGrid`, размер из токена `TouchTarget` | A3 |
| Редактор рецепта 16 полей | Группировка по смыслу (Tone / Color / Effects) со сворачиванием, одна кнопка Save | A10, A3 |
| Appearance-флайаут ×2 | Один компонент, пункт меню «Appearance» | A3 |
| Лента кадров 2 варианта | Один компонент `Filmstrip` (10 размеров в токенах), папка «Open» — слева в ленте | A8 |
| `BatchErrorsWindow` | Заменить встроенной панелью-тостом со списком; отдельное окно убрать | A7 |
| `Border.card` ×2, `layer` без стиля | Один `Surface.card` в `Controls.axaml` | A6 |
| Jargon («Plate», «Working quality», «Include note») | Переименовать по глоссарию из 01 | A5 |

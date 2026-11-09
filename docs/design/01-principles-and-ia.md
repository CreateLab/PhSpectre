# 01. Принципы и информационная архитектура

Решения ссылаются на пункты аудита (`00-audit.md`, A1…A11) и на источники: Lightroom Classic (модули + панели), Capture One (вкладки инструментов), darktable (группы модулей, поиск), ui-ux-pro-max (прогрессивное раскрытие, цели касания, motion-токены).

## Принципы

1. **Edit — главный экран.** Приложение открывается на фото в Edit. Всё остальное либо внутри него, либо отдельный тип работы (A1, A11).
2. **Один конвейер, один источник правды.** Фото → Look → Frame → Export. Frame рисуется из обработанного фото.
3. **На экране по умолчанию 5–7 контролов.** Остальное под «More» или показывается только когда выбран соответствующий тип (A2).
4. **Выбор глазами, не словами.** Look, рамки, стили карточки — миниатюры на **вашем** фото.
5. **Одна кнопка — одно действие, одно место.** Export только один раз (A3).
6. **Простой язык** (глоссарий ниже, A5).
7. **Неразрушающе.** Оригинал не меняется; Undo/Redo и «Before» всегда на виду.
8. **Холст нейтральный в обеих темах.**

## Режимы (верхняя панель)

```
 Edit · Collage · Camera
```

| Режим | Задача | Фото | Главная сущность |
|---|---|---|---|
| **Edit** (по умолчанию) | Сделать кадр: Look → Frame → Export | 1 | Кадр |
| **Collage** | Собрать несколько фото в одно | N | Лоток |
| **Camera** | Прочитать рецепт Fujifilm и записать в камеру | 1 | Рецепт |

**Почему Frame внутри Edit, а Collage снаружи.** Frame (обводка, карточка с палитрой и камерой, карточка рецепта) — шаг оформления того же одного кадра. Collage — другая модель: много фото, порядок, свой холст. В Lightroom по этой же причине Develop (один кадр) отделён от Print/Book (раскладки многих). Camera — отдельный процесс (подключение, запись), логика не меняется.

Прежний режим «Frame» (Card | Recipe) упразднён: Card и Recipe card становятся **типами рамки** внутри вкладки Frame.

## Раскладка (общая для десктопа)

```
┌ top bar ─────────────────────────────────────────────────────────┐
│ ◧ PhSpectre        Edit · Collage · Camera       ↶ ↷  ⋯  [Export]│
├───────────────────────────────────────────────┬──────────────────┤
│                                               │ [Look][Frame][Exp]│
│              canvas (neutral)                 │ tab content       │
│                                               │ (one topic only)  │
│    [Before] [grid] [Fit]                      │                   │
├───────────────────────────────────────────────┴──────────────────┤
│ filmstrip (always)  📁 Open  ▢ ▢ ▢ ▢ ▢ ▢ …                        │
└──────────────────────────────────────────────────────────────────┘
```

- Верхняя панель: логотип, режимы, Undo/Redo, «⋯» (Appearance, Updates, About), **Export** (одна кнопка).
- Холст: фото на нейтральном фоне; тулбар холста: Before, Composition grid, Fit.
- Правая панель 360 px (до 400 на широких экранах): три вкладки с иконками, подписи видны всегда.
- Лента кадров внизу всегда; в Collage с чекбоксами (A8).

## Edit: вкладки

### Look (по умолчанию)
1. **Amount** — один слайдер 0–100% для выбранного Look.
2. Строка слоёв: чипы «Preset: Moody 80% ✕», «LUT: Teal 100% ✕».
3. Предупреждение «Not applied: Clarity, Dehaze» — только если у пресета есть пропущенные параметры.
4. Поиск + фильтры **All · Favorites · Presets · LUTs · Mine** + «＋» (Import).
5. **Сетка миниатюр** Look на текущем фото (3 колонки), звезда избранного, бейдж PRESET / LUT.
6. Резерв: секция **Adjust** (скрыта; решение 2.5 = А).

### Frame
Сначала **тип рамки**, потом настройки **только этого типа**:

| Тип | Что показывается ниже |
|---|---|
| **None** | ничего |
| **Border** | плитки: Thin white · Gallery mat · Instant film · Square · Story …; «Customize» (Border, Bottom, Ratio, Color, Save as…) |
| **Card** | Card style (Dark · Light · Blurred photo · Custom); строки **Palette**, **Camera info** (Off · Short · Detailed), **QR code** — у каждой своё «More» |
| **Recipe card** | Style (Color · B&W · Minimal), рецепт из фото с «глазами» полей, Save to library, Send to camera |

Так из ≈ 60 контролов первого уровня остаётся 6–10 в зависимости от типа (A2, A4).

#### Когда тип рамки доступен

| Тип | Условие | Почему |
|---|---|---|
| **None**, **Border**, **Card** | всегда | работают с любым кадром |
| **Recipe card** | **в EXIF есть рецепт** (`RecipeReader` его прочитал) **и** на кадр не наложен ни пресет, ни LUT | карточка описывает, как кадр снят камерой; после Look картинка уже другая, и рецепт врал бы |

Как это выглядит для пользователя:
- Плитка Recipe card **не скрывается, а неактивна** (пунктирная рамка, приглушена), под выбором типа строка-причина: «Recipe card is unavailable: a Look is applied (Moody). Remove the Look to use it.» или «…no film recipe found in this photo (not a Fujifilm JPEG, or EXIF was removed).»
- Если Recipe card был выбран, а потом применили Look: тип автоматически меняется на **Card**, показывается тост «Recipe card needs the original look — switched to Card» с кнопкой **Undo** (возвращает и тип, и убирает Look).
- Если сменили фото на такое, где рецепта нет, а выбрана Recipe card: то же самое (переключение на Card, тост).
- **Camera info** в Card/Recipe показывает только то, что есть в EXIF; без EXIF строка неактивна («Camera info shows only what EXIF contains»). Palette работает всегда.
- В Look-вкладке при наличии выбранного Recipe card подсказка у «＋ / плиток»: «Applying a Look turns off Recipe card».

Логика доступности — одна чистая функция `FrameAvailability.Check(type, hasExifRecipe, lookApplied)` → (доступно, причина), чтобы её можно было покрыть тестами и использовать и в UI, и при пакетном экспорте (Export all: у фото без рецепта карточка рецепта заменяется на Card или пропускается с сообщением, как сегодня).

### Export
Format (JPEG/PNG), Quality, Size, Save to, «Show in folder after saving». Внизу **Export** и «Export all N photos…». About/Updates — в меню «⋯».

## Collage: инспектор
Вкладки **Layout · Content · Export**. Layout: плитки раскладок (Hero + 2, Grid, Strip), Gutter, Ratio. Лоток над холстом, чекбоксы на ленте.

## Глоссарий переименований

| Было | Стало |
|---|---|
| Process / Grade | **Edit** (вкладка внутри — **Look**) |
| Card / Recipe (режим) | типы рамки **Card**, **Recipe card** внутри Frame |
| Plate / Camera plate | **Camera info** |
| Guide lines (Golden, Cross) | **Composition grid** (Thirds, Golden ratio, Diagonal, Center) |
| Sampling: Vivid / Standard / Contrast | **Palette style**: Punchy / Natural / High contrast |
| Working quality | **Preview quality** (в «More» вкладки Export) |
| Include note | **Add a note** |
| Card (тема карточки) | **Dark / Light card** |
| Export settings… (пункт меню) | убрать: настройки лежат во вкладке **Export** |
| WB shift, Color Chrome, DR | оставляем (термины Fujifilm) |

## Состояния (обязательны для каждого экрана)
Пусто · Загрузка (скелетоны плиток) · Ошибка (причина + что сделать) · Успех (тост «Saved · Show»).

## Что намеренно ломаем
- Режимы Card, Recipe, Frame (и Process) как отдельные пункты верхней панели.
- Вкладки Style / Content / Export в старом Frame-инспекторе.
- `BatchErrorsWindow` как отдельное окно.
- Дубли Save/Export (остаётся один `ExportCommand` и один `ExportBar`).
- `ExportMode` внутри не трогаем: Card/Recipe/Collage рендеры остаются, меняется только то, как пользователь их выбирает (тип рамки → `ExportMode`).

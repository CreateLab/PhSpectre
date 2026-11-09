# 03. Экраны и эскизы

Живые эскизы: `mockups/index.html` (открыть в браузере; кнопка **Theme** переключает Dark/Light, `?theme=dark|light` в адресе). Готовые картинки: `previews/`.

| Экран | Макет | Превью |
|---|---|---|
| Edit · Look (главный) | `mockups/edit-look.html` | `previews/edit-look-dark.png`, `edit-look-light.png` |
| Edit · Frame (тип Card; внизу Border и Recipe card) | `mockups/edit-frame.html` | `previews/edit-frame-dark.png` |
| Edit · Export | `mockups/edit-export.html` | `previews/edit-export-dark.png` |
| Edit, телефон | `mockups/edit-phone.html` | `previews/edit-phone-dark.png` |
| Collage | `mockups/collage.html` | `previews/collage-dark.png` |
| Camera | `mockups/camera.html` | `previews/camera-dark.png` |
| Состояния | `mockups/states.html` | `previews/states-dark.png` |

## Edit · Look (десктоп ≥ 980)

```
┌ top ─────────────────────────────────────────────────────────────────────┐
│ ▣ PhSpectre          [Edit] Collage Camera              ↶ ↷  ⋯  [Export] │
├──────────────────────────────────────────────┬───────────────────────────┤
│                                              │ ◐ Look   ▢ Frame  ⇩ Export │
│                                              ├───────────────────────────┤
│                                              │ Amount                80% │
│            neutral canvas                    │ ━━━━━━━━━━━━●───          │
│            (#1A1A1C, любая тема)             │ [Preset Moody 80% ✕]      │
│                                              │ [LUT Teal Orange 100% ✕]  │
│                                              │ ⚠ Not applied: Clarity    │
│                                              │ [Search looks…]           │
│                                              │ All Fav Presets LUTs Mine ＋│
│     [Before] [▦ grid] [Fit]                  │ ┌───┐ ┌───┐ ┌───┐        │
│                                              │ │   │ │ ★ │ │   │ 3 колонки│
│  DSCF0412.JPG                                │ └───┘ └───┘ └───┘        │
│                                              │ ▸ Adjust (резерв)         │
│                                              ├───────────────────────────┤
│                                              │ [ Export JPEG… ]          │
├──────────────────────────────────────────────┴───────────────────────────┤
│ 📁 Open  ▢ ▢ ▢ ▢ ▢ ▢ ▢ …  (лента кадров)                                  │
└──────────────────────────────────────────────────────────────────────────┘
```

На экране сразу 7 элементов: Amount, строка слоёв, поиск, фильтры, сетка Look, Before, Export. Остальное во вкладках или за «▸».

**Горячие клавиши:** `Ctrl+Z / Ctrl+Y` — Undo/Redo, `\` (удерживать) — Before, `1–3` — вкладки, `←/→` — предыдущее/следующее фото, `Ctrl+S` — Export, `G` — Composition grid.

## Edit · Frame

Сначала выбор типа, потом настройки только этого типа.

```
Frame type:  [ None ] [ Border ] [ Card ] [ Recipe card ]

Border      → плитки (Thin white · Gallery mat · Instant film · Square 1:1 · Story 9:16 …)
              ▸ Customize: Border · Bottom · Ratio · Color · Save as…
Card        → Card style: Dark · Light · Blurred photo · Custom…
              Palette ☑        ▸ More (Colors, Palette style, Sort by)
              Camera info [Off|Short|Detailed]   ▸ Fields…
              QR code ☐        ▸ Caption · Content · Note
Recipe card → Style: Color · B&W · Minimal
              рецепт из фото (тап по полю скрывает его на карточке)
              [★ Save to library] [Send to camera]
```

**Доступность типов** (см. 01): None, Border и Card — всегда; **Recipe card — только если из EXIF читается рецепт и не применён пресет/LUT**. Иначе плитка неактивна, а под выбором типа объясняется причина. На макете `edit-frame.html` показаны три случая: Look наложен (главный экран), рецепт найден без Look, рецепта в EXIF нет.

Тулбар холста: Before, Composition grid (Thirds, Golden ratio, Diagonal, Center), Fit.

## Edit · Export

```
Format   [JPEG | PNG]          Quality  ━━━━●━  95
Size     ◉ Original 6240×4160   ○ Instagram 1080×1350   ○ Web 2048 px
Save to  C:\Users\…\PhSpectre  [Change]     ☑ Show in folder after saving
▸ Preview quality (More)
[ Export JPEG ]   [ Export all 12 photos… ]
```

Тот же `ExportCommand`, что и верхняя кнопка. После сохранения — тост «Saved · 4.2 MB · Show».

## Collage

```
┌ tray: ▢1 ▢2 ▢3  · Tick photos in the strip below · drag to reorder ┐
│ canvas: collage preview   [Regenerate] [Fit]  │ Layout · Content · Export │
│ filmstrip: чекбоксы вместо одиночного выбора                              │
```

Layout: плитки раскладок, Gutter, Ratio.

## Camera · три панели (как сейчас, новая оболочка)

```
┌ Recipes (320) ──────┬ Recipe (*) ────────────────┬ Camera (340) ─────────┐
│ Photos|Saved|Popular │ Name · source              │ X-T5 ● Connected     │
│ search · chips       │ 3×3 параметров             │ Slots C1 C2 C3       │
│ list                 │ [Edit] [★ Save]            │ [Write recipe to C1] │
│                      │                            │ Restore points ▾     │
└──────────────────────┴────────────────────────────┴──────────────────────┘
```

Состояния подключения и окна записи (Warning · Writing · Done · Error) не меняются по логике, получают общие стили.

## Телефон (< 840)

```
┌ ▣ PhSpectre        ↶  [Export] ┐
│          фото                 │
├ ═══ (шторка 48%) ─────────────┤
│ Amount 80%  ━━━━●──           │
│ All Fav Presets LUTs          │
│ ▢ ▢ ▢ ▢   (4 колонки)         │
├───────────────────────────────┤
│ Look  Frame  Export  Photos ⋯ │
└───────────────────────────────┘
```

- Нижняя панель: три шага Edit (Look · Frame · Export), Photos и More. Режимы Edit / Collage / Camera, Appearance, Updates, About — в «More».
- Шторка показывает **одну** тему; закрывается свайпом вниз; цели ≥ 48 dp.
- «Photos» открывает шторку с сеткой кадров, а не постоянную полосу.

## Состояния
См. `mockups/states.html`: нет фото, пустая библиотека, загрузка миниатюр, ошибка плитки, результат импорта (тост + «Details»), экспорт (Saving… / Saved / Couldn't save).

## Замечания по эскизам
- Фильтры-чипы в 360 px не умещаются все (виден «Mine» обрезанным): строка прокручивается; на 400 px помещается. Решить при реализации (спрятать «Mine» за «＋ More» или уменьшить поля).
- Две плитки «Loading…» показаны для скелетонов; в реальной сетке миниатюры считаются в фоне в порядке видимости.
- В Collage плитки раскладок на эскизе пустые: реальные миниатюры рисуются при реализации.

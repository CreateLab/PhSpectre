# PhSpectre design system

Source of truth is `PhSpectre.Avalonia/Styles/`. This file only explains how the pieces fit.

| File | Contents |
|---|---|
| `Colors.axaml` | Colour tokens, theme-aware (`ThemeDictionaries` Dark / Light). Always `DynamicResource`. |
| `Tokens.axaml` | Non-colour tokens: spacing (4-pt scale `Space1..6`), radius (`RadiusSm..Xl`), type scale (`FontCaption..Title`), `TouchTarget` (44), motion durations, breakpoints. Plain values, `StaticResource` is fine. |
| `Typography.axaml` | Inter (app default) for UI, JetBrains Mono for data (`mono-data`). |
| `Buttons.axaml`, `Controls.axaml` | Component kit: `cta`, `primary`, `link`, segmented `seg`, `chips`, `switch`, `filmstrip`, ... |

## Colour roles
- Canvas / photo areas use `StageBrush` (neutral); the accent (`AccentBrush`) appears only on active elements.
- Surfaces: `BackgroundBrush` < `PanelBrush` / `SurfaceBrush` < `ControlBrush` < `ControlSelectedBrush`.
- Text: `TextPrimaryBrush`, `TextSecondaryBrush`, `TextMutedBrush` (muted is for hints, not for essential labels).
- Status: `SuccessBrush`, `WarningBrush`, `ErrorBrush`.

## Rules
- Breakpoints: phone < 600 <= tablet < 1000 <= desktop. Phone / tablet touch targets are at least 44 x 44.
- Every new interactive control sets `AutomationProperties.Name` when it has no visible text.
- Text contrast should meet WCAG AA (4.5:1 body, 3:1 large text). Not audited yet for the existing palette.
- Every new screen defines an empty, loading and error state.

## Modes
Top-bar segment indices: 0 Edit, 1 Collage, 2 Camera (`SettingsViewModel.TopModeIndex`). Edit has tabs Look · Frame · Export
(`EditTab`) and frame types None · Border · Card · Recipe card (`FrameType`, availability in `FrameAvailability`).
The Edit canvas/inspector show while `IsProcessMode`; Card / Recipe card and Collage use the legacy card workspace
(`ShowCardBody`). The photo on screen is shared via `CurrentPhotoProvider`. See `docs/design/`.

# Opacbar style guide

Modern, minimal, monochrome. The UI should feel like a native Windows 11 flyout that happens to be black and white.
These tokens are implemented one-to-one in `src/ThemeAndControls.cs` (`Theme` static class).

## Color

The brand is monochrome, and that applies to **Opacbar's own UI only**. Chrome is black, white and grays; color appears only to mean something (success, warning, error), and only sparingly:
a status dot, an error-red "Quit". Toggles and selections are white (dark theme) or near-black (light theme), never green. The one exception is user content: the taskbar color picker and its swatches show real colors, because the user is choosing a color.

| Token | Dark | Light | Use |
|---|---|---|---|
| Surface | `#1F1F1F` | `#FAFAFA` | Window background |
| Raised | `#2B2B2B` | `#ECECEC` | Segmented track, button hover |
| Hover | `#383838` | `#E0E0E0` | Hovered/pressed segment, slider track |
| Border | `#3D3D3D` | `#D9D9D9` | 1px window and button outline |
| Text | `#FFFFFF` | `#111111` | Primary text |
| Text secondary | `#A6A6A6` | `#5E5E5E` | Hints, values, section headers |
| Text disabled | `#666666` | `#AAAAAA` | Disabled text |
| Track off | `#8A8A8A` | `#7A7A7A` | Toggle outline and knob when off |
| On fill | `#FFFFFF` | `#111111` | Toggle on, selected segment, slider fill and thumb |
| On fill text | `#000000` | `#FFFFFF` | Text/knob drawn on "on fill" |
| Success | `#3FB950` | `#1A7F37` | Status dot: running |
| Warning | `#E3B341` | `#9A6700` | Status dot and note: needs attention |
| Error | `#F85149` | `#CF222E` | Status dot and note: failed; destructive text |

The theme follows Windows app mode (`AppsUseLightTheme`) unless the user picks Dark or Light in General.
Disabled controls blend their ink 60% toward Surface.

## Type

Font: **Segoe UI Variable Text**, falling back to **Segoe UI**.
Sizes are in points; render as pixels = pt x 96/72 x scale (`GraphicsUnit.Pixel`). One scale factor only: window DPI / 96.

| Style | Size | Weight | Use |
|---|---|---|---|
| Title | 14 pt | Bold | "Opacbar" |
| Body | 10 pt | Regular | Labels, buttons, values, menu |
| Caption | 9 pt | Regular / Bold | Hints, status, section headers (bold), segment text |
| Small | 8 pt | Regular | Footer |

## Spacing and shape

Spacing steps (logical px): **4, 8, 12, 16, 24**.
Window padding 16. Gap between blocks 12. Gap between rows 4. Gap between buttons 8.

Radii: window 8 (set by Windows 11), segmented track 8, segment/button 6, menu item 4, toggle and slider thumb fully round.
Borders are 1 px, color Border.

## Window

Flyout, **380 logical px wide**, height fits content (max 760, and never taller than the work area). Anchored to the bottom-right of the working area of the screen
with the cursor, 12 px from the edges. Rounded corners (DWM), 1 px border, drop shadow.
Hides on focus loss (not while being dragged or while a dialog is open) and on Esc. The header drags the window.

## Controls

| Control | Spec |
|---|---|
| Toggle | 40 x 20. Off: 1 px outline + small knob, both Track off. On: filled On fill with On fill text knob. Space/Enter toggles. |
| Segmented | 32 high, Raised track, equal-width segments inset 2. Selected: On fill with On fill text. Hover: Hover. Left/Right arrows move. |
| Slider | Label left, value right (secondary), track 4 high, thumb 16 round. Arrows, PageUp/Down, Home/End. |
| Row | 36 high. Label left, one control right. Clicking the row toggles its toggle. |
| Section header | Caption bold, secondary, 24 high, text at the bottom. |
| Button | 36 high, full width, 1 px Border, Raised on hover. Danger buttons use Error-colored text, never an error-colored fill. |
| Status | 8 px dot + Caption text. Dot is Success, Warning, Error, or gray when off/paused. |
| Color row | 36 high. Label "Color" left; right: 20 px rounded color chip (1 px Track off outline), hex in secondary text, chevron. Click or Space/Enter expands the picker. Hidden for Clear. |
| Color picker | Inline panel under the color row, 1 px Border, radius 8, padding 8. Saturation/value square (120 high, radius 6, thumb 14 px white ring with dark halo), hue strip (14 high, fully round), hex box (120 x 28, Raised, 1 px Border; Error-colored 1.5 px border and "Use #RRGGBB" when invalid), then a swatch row of 22 px chips: black, white, three grays, a gap, then up to 6 recent colors (selected chip gets a 1.5 px Text ring). Everything is owner-drawn from the single DPI scale. Dragging applies live (helper sends are throttled to about 30 per second); recents are saved when a drag ends, a swatch is clicked, or a hex value is committed. |
| Hint | Caption, secondary. Under Blur: "Needs Windows transparency effects on"; shown with a Warning dot when Windows transparency effects are off. |
| Focus | 1.5 px Text-colored ring, only when navigating by keyboard. |

## Voice and copy

- Short and plain. Say what it does, not how it works.
- Sentence case everywhere ("Hide top line", not "Hide Top Line").
- No jargon: not "luminance", "accent policy", "alpha", "backdrop". Use "color", "opacity", "style".
- Verbs for actions: "Open settings file", "Reset to defaults", "Quit and restore taskbar".
- Style names: Clear, Color, Blur. One look for everything (no per-situation looks).
- Errors say what happened and what to do, in one line. No exclamation marks, no emoji.
- Footer: "Open source · no network · no admin". Emergency quit: Ctrl+Shift+Alt+Q.


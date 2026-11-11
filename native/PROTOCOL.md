# Opacbar ⇄ OpacbarTAP protocol (v2)

The helper DLL (`OpacbarTAP.<arch>.dll`) runs inside explorer.exe. On every XAML UI thread
it creates one message-only window of class `OpacbarTAP.Sink` (parent `HWND_MESSAGE`) at the first visual-tree
callback seen on that thread, plus one **control sink** on the helper's own thread right after injection
(it exists even if no XAML callback ever arrives). Opacbar.exe finds them with
`FindWindowEx(HWND_MESSAGE, prev, "OpacbarTAP.Sink", null)`. Use STATS to tell taskbar sinks (high word > 0)
from other XAML threads (e.g. File Explorer windows) and from the control sink; sending RULES/RESTORE to every
sink is harmless (non-taskbar threads apply nothing).

All messages are sent with `SendMessageTimeout(SMTO_ABORTIFHUNG, 1000 ms)`.

| Message | wParam | lParam | Returns |
|---|---|---|---|
| `WM_APP+0x12` PING | 0 | 0 | `0x0BAC` |
| `WM_APP+0x15` VERSION | 0 | 0 | protocol version (currently `6`). `3` = v2 rules + STATS + UNLOAD, `4` = tree service via IXamlDiagnostics, `5` = exact restore of original brushes, `6` = `@blur:` values; the first v2 build returned `2` and has neither STATS nor UNLOAD. v1 helpers return 0. Helpers returning 0 or 2 cannot be unloaded → Explorer restart needed; ≥ 3 → send UNLOAD, then inject the new copy |
| `WM_APP+0x11` RESTORE | 0 | 0 | number of properties restored **on the receiving sink's thread**. Undoes every change, rule set becomes empty |
| `WM_COPYDATA` RULES | sender HWND | `COPYDATASTRUCT{ dwData = 0x4F504143 ('OPAC'), lpData = UTF-16 text, cbData = bytes }` | number of (element, property) pairs currently applied **on the receiving sink's thread** (sum over sinks for a total), or -1 on a bad header / wrong `dwData` / document > 256 KB |
| `WM_APP+0x14` DUMP | HWND of an Opacbar window | 0 | 1 if the dump was delivered. The helper sends `WM_COPYDATA{ dwData = 0x4F545245 ('OTRE'), UTF-16 text, not NUL-terminated }` to that HWND (synchronously, before returning; the helper waits up to 10 s). Reading every property can take a while: use a longer timeout (e.g. 15 s) for DUMP, or keep pumping messages after a timeout — the `OTRE` message still arrives |
| `WM_APP+0x16` STATS | 0 | 0 | bits 0–15: elements known on this sink's thread; bits 16–31: of those, elements inside taskbar trees (both saturate at 0xFFFF; on the control sink both counts are process-wide). 64-bit helpers add state bits: 32 = AdviseVisualTreeChange succeeded, 33 = it failed, 34 = still in progress, 35 = this is the control sink, 36 = unloading |
| `WM_APP+0x17` UNLOAD | 0 | 0 | 1 if the unload was performed/scheduled, 0 if not possible right now (not subscribed yet, already unloading). Send it to **one** sink. Before returning, every thread's applied properties are restored (other sinks are asked synchronously, 2 s each) and the rule set is emptied; then the sinks are destroyed and the control thread unsubscribes (`UnadviseVisualTreeChange`), waits for in-flight callbacks, releases the XAML interfaces and drops the helper's module reference (the code is actually unmapped only once Explorer holds no object of the helper). An unloaded DLL copy stays inert even if injected again: inject a newly named copy (e.g. the hashed file name of the new version). Helpers older than this build pinned themselves and have no UNLOAD (they return 0): those still need an Explorer restart |


v1 messages (`WM_APP+0x10` SET) remain supported for compatibility but Opacbar v2 no longer uses them.
Internally SET is translated to the rules document
`Taskbar.TaskbarBackground > *#BackgroundFill\tFill\t#AARRGGBB` (+ `...#BackgroundStroke\tFill\t#00000000`).

Each sink applies rules only to elements owned by its own UI thread. The rule set itself is shared: a document
received by any sink replaces the rule set for all sinks, and the other sinks re-apply asynchronously (Opacbar
still sends the document to every sink, which makes the result synchronous for each of them).

## Rules document (UTF-16, `\n` separated, fields separated by `\t`)

```
opacbar-rules 2
<selector>\t<property>\t<value>
...
```

- Sending a document **replaces** the whole previous rule set. Properties that were set before but are no longer
  targeted are restored to their original value (original local value re-set, else `ClearProperty`).
- New elements that appear later (new monitor, Explorer recreating the bar) get the current rules applied.
- An empty document (just the header) = restore everything.
- Lines without two tabs, with an unparsable selector or an empty property/value are ignored. If several rules
  target the same (element, property), the later line wins. Only elements inside taskbar trees (see Dump) are
  ever modified. A rule whose property does not exist on a matched element is skipped for that element.
- Re-sending a document re-applies every value (useful if Windows overwrote one).

### Selector

A chain of segments separated by ` > ` (direct parent → child). The **last** segment is the target; earlier
segments must match its direct ancestors in order (the first segment may be any distance above — i.e. the
chain is anchored at the target and walks up through *direct* parents, except the first segment which matches
the nearest ancestor satisfying it).

Segment forms:
- `Type` — full XAML type name, e.g. `Windows.UI.Xaml.Shapes.Rectangle`, `Taskbar.TaskbarBackground`
- `Type#Name` — type + `x:Name`
- `*#Name` — any type with that name
- `Type[N]` / `Type#Name[N]` — optional child index N (0-based) among the parent's children
  (the index XAML reported when the element was added; it is not renumbered when siblings change)
- `*` — any element (useful as a middle segment); `Type#` — element of that type without an x:Name
- Type and name matching is exact and case-sensitive. Whitespace around `>` is optional.

### Value

- `@clear` — ClearProperty (use the template/style value).
- `@blur:<radius>:<#AARRGGBB>` (protocol ≥ 6) — an adjustable-strength blur brush made by the helper: a
  `XamlCompositionBrushBase` whose composition brush is a Gaussian blur of what lies behind the element
  (`Compositor.CreateBackdropBrush`) with the tint composited on top (same technique as Windhawk's
  "Windows 11 Taskbar Styler" `WindhawkBlur` brush). Intended for brush properties, typically
  `Taskbar.TaskbarBackground > *#BackgroundFill\tFill\t@blur:30:#40202020`.
  - `radius` — blur amount in DIPs (Gaussian standard deviation), decimal, `0`–`100` (larger values are
    clamped). `0` = no blur: the tint over the unblurred backdrop (radius 0 with a fully transparent tint =
    clear).
  - tint — `#AARRGGBB` (or `#RRGGBB` = opaque), straight alpha, drawn over the blur. Optional: `@blur:30`
    = blur without tint. The tint is also the brush's `FallbackColor` (shown when Windows disables
    effects, e.g. transparency effects off / battery saver).
  - optional suffix `:host` (`@blur:30:#40202020:host`) samples the host backdrop
    (`CreateHostBackdropBrush`, what's behind the window) instead of the in-tree backdrop. Experimental.
  - Malformed `@blur:` values make the line be ignored. Each element gets its own brush, kept alive by the
    helper while applied; restore puts the original value back exactly like for XAML values.
- starts with `<` — XAML, created with `Windows.UI.Xaml.Markup.XamlReader.Load`. The helper injects
  `xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"` and `xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"`
  into the root element if missing. Example:
  `<AcrylicBrush BackgroundSource="HostBackdrop" TintColor="#FF202020" TintOpacity="0.4" TintLuminosityOpacity="0.3" FallbackColor="#99202020"/>`
- anything else — a plain string value, created with `IVisualTreeService::CreateInstance(<property ValueType from GetPropertyValuesChain>, value)`,
  e.g. `Margin` → `4,0,4,6`, `CornerRadius` → `12`, `Opacity` → `0.5`, `Visibility` → `Collapsed`, `Fill` → `#00000000`.
  Type tried, in order: the property's declared `Type` (an abstract `…Media.Brush` is replaced by
  `…Media.SolidColorBrush`), then the current value's `ValueType`, then (for values starting with `#`)
  `Windows.UI.Xaml.Media.SolidColorBrush`.
- Each element gets its own XAML-created object (so non-shareable values such as UIElements work); plain values
  are cached and shared per UI thread.

## Dump format (`OTRE`)

```
opacbar-tree 2
<depth>\t<type>\t<name>\t<childIndex>\t<ActualWidth>x<ActualHeight>\t<Margin>\t<Padding>\t<CornerRadius>\t<Background or Fill>
```
One line per element in the taskbar trees (every tree that contains a `Taskbar.TaskbarFrame` or
`SystemTray.SystemTrayFrame` element; the dump starts at the topmost known ancestor of that frame) owned by the
receiving sink's thread, depth-first, children in child-index order. Missing values are empty; tabs/newlines
inside values are replaced by spaces. `<Background or Fill>` is the effective `Background` (else `Fill`): for a
brush object it is the short type name plus its `Color` if it has one (e.g. `SolidColorBrush #FF202020`,
`AcrylicBrush #CC202020`), otherwise the raw value string. At most 20000 lines (and 8 M characters).

## Safety rules for the helper
- No file, registry or network access. All data in/out goes through the messages above.
- Never throw across COM; every HRESULT checked; never crash Explorer.
- Max rules document size 256 KB; max 512 rules (extra lines ignored); max 16 selector segments; max 4096
  applied (element, property) pairs per process.

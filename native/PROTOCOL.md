# Opacbar ⇄ OpacbarTAP protocol (v2)

The helper DLL (`OpacbarTAP.<arch>.dll`) runs inside explorer.exe. On every XAML UI thread
that hosts a taskbar it creates one message-only window of class `OpacbarTAP.Sink`
(parent `HWND_MESSAGE`). Opacbar.exe finds them with `FindWindowEx(HWND_MESSAGE, prev, "OpacbarTAP.Sink", null)`.

All messages are sent with `SendMessageTimeout(SMTO_ABORTIFHUNG, 1000 ms)`.

| Message | wParam | lParam | Returns |
|---|---|---|---|
| `WM_APP+0x12` PING | 0 | 0 | `0x0BAC` |
| `WM_APP+0x15` VERSION | 0 | 0 | protocol version (`2`). v1 helpers return 0 → Opacbar asks the user to restart Explorer to load the new helper |
| `WM_APP+0x11` RESTORE | 0 | 0 | number of properties restored **on the receiving sink's thread**. Undoes every change, rule set becomes empty |
| `WM_COPYDATA` RULES | sender HWND | `COPYDATASTRUCT{ dwData = 0x4F504143 ('OPAC'), lpData = UTF-16 text, cbData = bytes }` | number of (element, property) pairs currently applied **on the receiving sink's thread** (sum over sinks for a total), or -1 on a bad header / wrong `dwData` / document > 256 KB |
| `WM_APP+0x14` DUMP | HWND of an Opacbar window | 0 | 1 if the dump was delivered. The helper sends `WM_COPYDATA{ dwData = 0x4F545245 ('OTRE'), UTF-16 text, not NUL-terminated }` to that HWND (synchronously, before returning; the helper waits up to 10 s). Reading every property can take a while: use a longer timeout (e.g. 15 s) for DUMP, or keep pumping messages after a timeout — the `OTRE` message still arrives |

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

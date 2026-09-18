# Loopayeh Design Tokens (single source of truth)

Shared by PKG Viewer (tkinter), PKG Sender and LoopDPI-Sender (Avalonia).
Viewer is the reference implementation; the Avalonia apps mirror it.

## Colors

| Role   | Value     | Usage                              |
|--------|-----------|------------------------------------|
| BG     | `#171717` | window background                  |
| CARD   | `#202020` | cards, header/footer bars, inputs* |
| CARD2  | `#2A2A2A` | inputs, selected rows, sub-panels  |
| ACCENT | `#4F8EF7` | primary buttons, highlights, links |
| TEXT   | `#F1F3F8` | primary text                       |
| MUTED  | `#8B93A5` | secondary text, labels             |

*viewer entries are flat (`relief=flat`, bg=CARD); Avalonia inputs use CARD2 + ACCENT focus border.
Text on ACCENT is always `#171717`.

Button states (from viewer `_RBTN_FACE`):
- accent: face `#4F8EF7` / hover `#6FA8FF` / pressed `#3B70C9`
- ghost: face `#404040` / hover `#2C3342` / pressed `#333A44`
- disabled: face `#2A2A2A`, text `#8B93A5`

Semantic badge colors (keep as-is, do NOT normalize):
green `#8FD694`/`#10B981`/`#3DD6B0`/`#6FD3C9`, amber `#F59E5B`/`#E8A34C`/`#F2B84B`,
red `#E17B7B`, purple `#9B6DDB`, light blue `#91C8F6`, gray `#6B7280`.

## Typography (Segoe UI everywhere)

| Role    | Viewer (tkinter)      | Avalonia              |
|---------|-----------------------|-----------------------|
| Title   | 18 bold (FONT_BIG)    | 18 bold               |
| Body    | 10 (FONT)             | 11–12 regular         |
| Emphasis| 11 bold (FONT_MID)    | 12–13 bold            |
| Small   | 9 (FONT_SMALL)        | 10–11, MUTED          |
| Badge   | 9 bold (FONT_BADGE)   | 11 bold, ACCENT       |
| Button  | 10 (pad 16,9 / 12,7)  | 12 bold (pad 16,9)    |
| Mono    | Consolas 9 (dumps)    | —                     |

## Shape

- Buttons/inputs radius 4. Cards radius 8. No shadows; flat surfaces separated by tone only.
- Window chrome: native frames on both toolkits (no `overrideredirect`, no `ExtendClientArea`).
- tkinter dark titlebar recipe (DWMWA_USE_IMMERSIVE_DARK_MODE): call AFTER
  `deiconify()` + full `update()` on `GetParent(root.winfo_id())` — `winfo_id()`
  alone is the Tk client child, and a withdrawn window has no HWND yet
  (both cases give silent `E_HANDLE`). Verified `rc20=0` on Win11 24H2.

# CleanDesk

**English** | [Русский](README.ru.md)

A tiny Windows tray app for a clean, minimal desktop: hide the taskbar and the desktop icons with a hotkey, and bring them back with the same hotkey.

| Action | Default hotkey |
|---|---|
| Hide / show the taskbar | **Ctrl+Alt+L** |
| Hide / show desktop icons | **Ctrl+Alt+K** |

Windows has a built-in "Automatically hide the taskbar" option, but the taskbar keeps popping up every time the mouse touches the bottom of the screen. With CleanDesk the taskbar stays hidden until you press the hotkey again, and maximized windows use the whole screen.

## Features

- Desktop icons are toggled with the same switch as *right-click the desktop → View → Show desktop icons*.
- Hotkeys are set in a plain text file and apply as soon as you save it.
- Optional start with Windows.
- No installer and no dependencies. It's a single ~130 KB exe that uses .NET Framework 4, which is already part of Windows 10 and 11.

- **Ctrl+Alt+L** hides or shows the taskbar. Left-clicking the tray icon does the same.
- **Ctrl+Alt+K** hides or shows the desktop icons.
- Right-click the tray icon for a menu with both toggles, **Settings…**, **Start with Windows** and **Exit**.
- When you exit, the taskbar comes back. Desktop icons are a regular Windows setting, so they stay as they are after you exit.

## Settings

`CleanDesk.ini` is created next to the exe on first launch. You can also open it from the tray menu → **Settings…**. Save the file and CleanDesk applies the changes within a second, with no restart needed.

```ini
; Interface language: auto (same as Windows), en or ru
Language = auto

; Start CleanDesk when you sign in to Windows: yes or no
StartWithWindows = no

; Hide / show the taskbar
Taskbar = Ctrl+Alt+L

; Hide / show desktop icons
DesktopIcons = Ctrl+Alt+K
```

- A hotkey is a list of modifiers plus one key: `Ctrl+Alt+L`, `Ctrl+Shift+F1`, `Win+Alt+H`, `F9`…
- Modifiers: `Ctrl`, `Alt`, `Shift`, `Win`.
- Keys: `A`–`Z`, `0`–`9`, `F1`–`F24`, `Space`, `Enter`, `Tab`, `Esc`, `Backspace`, `Insert`, `Delete`, `Home`, `End`, `PageUp`, `PageDown`, arrows (`Up`, `Down`, `Left`, `Right`), `NumPad0`–`NumPad9`, `Pause`, `PrtSc`, and ``- = [ ] ; ' , . / \ ` ``. Write the plus key as `Plus`.
- Letters mean physical keys, so Cyrillic letters work too: `Ctrl+Alt+Д` is the same as `Ctrl+Alt+L`.
- `none` turns an action off.
- If a hotkey has a typo or another program already uses it, a notification explains what's wrong. If that happens while the taskbar is hidden, the taskbar is shown right away so you're never stuck without it.

## Build from source

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The result is `bin\CleanDesk.exe`. Nothing needs to be installed: the C# compiler (`csc.exe`) ships with Windows as part of .NET Framework 4.x. The icon is `assets/icon.ico`. It is drawn by `assets/make-icon.ps1`, so you can tweak that script and redraw the icon.

## How it works

- **Taskbar.** Hiding turns on the regular *auto-hide* option, so the work area becomes the whole screen. It also hides the taskbar windows (`Shell_TrayWnd`, `Shell_SecondaryTrayWnd`) with `ShowWindow(SW_HIDE)`, so they can't slide out. While hidden, a watchdog hides them again if Explorer brings them back. Showing the taskbar restores the original auto-hide setting. That value is also kept in `HKCU\Software\CleanDesk` while the taskbar is hidden, for crash recovery.
- **Desktop icons.** CleanDesk sends Explorer's desktop view (`SHELLDLL_DefView`) the same `WM_COMMAND` (`0x7402`) as the *Show desktop icons* menu item.
- **Hotkeys.** `RegisterHotKey`; `CleanDesk.ini` is re-read when its timestamp changes.
- **Autostart.** A value in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

## License

[MIT](LICENSE)

---

Made with AI help

# CashGear Capture

A Windows desktop screen capture and annotation tool (in the spirit of FastStone Capture), built with C# / .NET 10 / WPF.

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Scaffold, tray, DI/logging/settings, single instance | done |
| 1 | Monitor/window enumeration, full-screen capture, save + clipboard | done (mixed-DPI hardware check pending) |
| 2 | Region selection overlay (drag, window pick, loupe, keyboard nudge) | done (mixed-DPI hardware check pending) |
| 3 | Window capture (WGC, occluded windows, optional shadow) + global hotkeys | done |
| 4–9 | Settings UI + post-capture actions, editor, scrolling capture, recording, OCR, packaging | planned |

See [docs/CHANGELOG.md](docs/CHANGELOG.md) for what each phase delivered and [docs/TESTING.md](docs/TESTING.md) for the manual test matrix.

## Requirements

- Windows 10 2004 (build 19041) or later, x64
- .NET SDK 10.0.401+ (pinned in `global.json`)

## Build, test, run

```powershell
dotnet build CashGear.Capture.sln
dotnet test  --solution CashGear.Capture.sln
dotnet run --project src/CashGear.Capture.App
```

Interactive capture tests need a logged-in desktop; set `CASHGEAR_SKIP_INTERACTIVE=1` to skip them (e.g. on CI).

The app starts hidden in the system tray. Command-line switches forward to the running instance:

| Switch | Effect |
|---|---|
| *(none)* | Start, or tell the running instance to show an "already running" notice |
| `--capture-fullscreen` | Capture every monitor plus the stitched virtual screen |
| `--capture-region` | Open the region overlay (drag a rectangle or click a window, Enter to capture, Esc to cancel) |
| `--capture-window` | Open the window picker (click a window to capture its own content) |
| `--exit` | Shut the running instance down |

## Hotkeys

| Default | Action |
|---|---|
| `PrintScreen` | Capture Region |
| `Alt+PrintScreen` | Capture the active window |
| `Ctrl+PrintScreen` | Capture Full Screen |
| `Ctrl+Shift+PrintScreen` | Scrolling capture (Phase 6) |

Change them in `settings.json` under `hotkeys` (e.g. `"captureRegion": "Ctrl+Shift+F9"`, `null` to disable); Settings… in the tray opens the file, and saved edits apply immediately. Keys another app or Windows already owns are reported in a notification.

## Layout

```
src/CashGear.Capture.Core       Models, interfaces, settings, pixel math (net10.0, no WPF/WinRT)
src/CashGear.Capture.Interop    All P/Invoke + COM declarations
src/CashGear.Capture.Engine     Capture engines (WGC, GDI), monitor/window enumeration
src/CashGear.Capture.Editor     Annotation editor model (Phase 5)
src/CashGear.Capture.Recording  Screen recording (Phase 7)
src/CashGear.Capture.App        WPF app: tray, windows, view models, output
tests/                          xUnit v3 + AwesomeAssertions (Core, Editor, Engine, App)
```

## Files on disk

| What | Where |
|---|---|
| Settings | `%AppData%\CashGear\Capture\settings.json` |
| Logs | `%LocalAppData%\CashGear\Capture\logs\` |
| Captures (default) | `Pictures\CashGear Capture\` |

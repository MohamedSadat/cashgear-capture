# CashGear Capture

A Windows desktop screen capture and annotation tool (in the spirit of FastStone Capture), built with C# / .NET 10 / WPF.

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Scaffold, tray, DI/logging/settings, single instance | done |
| 1 | Monitor/window enumeration, full-screen capture, save + clipboard | done (mixed-DPI hardware check pending) |
| 2 | Region selection overlay (drag, window pick, loupe, keyboard nudge) | done (mixed-DPI hardware check pending) |
| 3 | Window capture (WGC, occluded windows, optional shadow) + global hotkeys | done |
| 4 | Settings window, after-capture actions, file-name templates, capture delay, start with Windows, light/dark | done |
| 5 | Editor: annotations, crop/rotate/resize/border/shadow, undo/redo, zoom/pan, save/copy | done |
| 6 | Scrolling capture: UI Automation or mouse-wheel scrolling, exact row stitching | done |
| 7 | Screen recording to MP4 (ffmpeg, H.264), pause, click highlights, toolbar | done |
| 8–9 | OCR, packaging | planned |

See [docs/CHANGELOG.md](docs/CHANGELOG.md) for what each phase delivered and [docs/TESTING.md](docs/TESTING.md) for the manual test matrix.

## Requirements

- Windows 10 2004 (build 19041) or later, x64
- .NET SDK 10.0.401+ (pinned in `global.json`)
- For screen recording: ffmpeg. Run `tools\get-ffmpeg.ps1` once (it downloads a pinned, checksum-verified build into the git-ignored `tools\ffmpeg`); the build copies it next to the app as `ffmpeg\`. Without it everything else works and recording says what is missing.

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
| `--capture-scrolling` | Open the window picker, then scroll the clicked window to the bottom and stitch one tall image |
| `--record` | Start a screen recording (pick the area), or stop the one running |
| `--exit` | Shut the running instance down |

## Hotkeys

| Default | Action |
|---|---|
| `PrintScreen` | Capture Region |
| `Alt+PrintScreen` | Capture the active window |
| `Ctrl+PrintScreen` | Capture Full Screen |
| `Ctrl+Shift+PrintScreen` | Scrolling capture |
| `Shift+PrintScreen` | Start a screen recording / stop it |

Change them in **Settings › Hotkeys** (click a box and press the keys) or in `settings.json` under `hotkeys`. A key another app or Windows already owns is reported, and a single typing key (e.g. `A`) is refused because it would stop working everywhere else.

## Settings

Tray › **Settings…** opens the settings window (light/dark follows Windows, or choose one):

| Tab | What |
|---|---|
| Output | After capture: open in the editor / save to file / copy to clipboard (any combination); folder; PNG/JPEG/BMP and JPEG quality; file-name template with a live example |
| Capture | Capture delay, hide delay, cursor, window shadow, full-screen output, scrolling capture (wait after each scroll, height limit), capture method |
| Recording | Frames per second (15/30/60), quality, encoder (hardware when available, or x264), pointer, click highlights, where ffmpeg is |
| Hotkeys | Record hotkeys; each shows whether it is active |
| General | Start with Windows, theme, open the settings file |

Everything is stored in `%AppData%\CashGear\Capture\settings.json`; edits to the file apply as soon as it is saved.

File-name tokens: `{yyyy} {MM} {dd} {HH} {mm} {ss}` (capture time), `{counter}` (0001, 0002, … kept across restarts), `{kind}` (fullscreen, monitor, region, window, scrolling, recording), `{monitor}` (e.g. DISPLAY1), `{window}` (window title). Default: `Capture_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}_{kind}`.

## Scrolling capture

Tray › **Scrolling Capture** (or `Ctrl+Shift+PrintScreen`): click the window on the picker, pointing at the part that scrolls. It is scrolled from where it is now down to the bottom, and the frames are stitched into one image that then goes to the editor, the folder or the clipboard as usual. Scroll to the top first to capture everything.

- Apps with UI Automation scrolling (Notepad, most Windows apps) are scrolled directly; others (Chrome, here) with the mouse wheel. The mouse goes back where it was afterwards.
- A small progress window (left out of the capture) shows the progress. **Finish now** keeps what has been captured; **Esc** or **Cancel** abandons it.
- Toolbars and status bars that don't scroll appear once; the scrollbar is cut off.

## Screen recording

Tray › **Record Screen…** (or `Shift+PrintScreen`): drag the area to record, or click a window or a monitor, and press Enter. A small toolbar at the top of the screen shows the time and has **Pause** and **Stop**; it is never in the video. Stop (or the hotkey again) saves an MP4 (H.264) to the capture folder and says how long and how big it is.

- One monitor at a time (Windows.Graphics.Capture); an area that spans two is limited to the monitor holding its centre. On Windows 10 Windows draws a yellow border around the recorded monitor.
- The encoder is chosen by trying them: NVIDIA, Intel or AMD hardware when it works, otherwise x264 on the CPU.
- The video has a constant frame rate; when the screen is still, the same picture repeats. If encoding falls behind, frames are skipped and the notification says how many.

## Editor

With **Open in the editor** on (the default for new installs), every capture opens in its own editor window. Annotations stay editable objects until you save or copy; then they are burned into the image.

| Tool | Key | Notes |
|---|---|---|
| Select | `V` | Click to select, drag to move, grips to resize (Shift keeps proportions); double-click text to edit it |
| Arrow, Line | `A`, `L` | Shift snaps to 45° |
| Rectangle, Ellipse | `R`, `E` | Outline colour, fill, line width; Shift for a square or circle |
| Pen | `D` | Freehand |
| Highlight | `H` | Highlighter colour multiplied into the image, so text under it stays readable |
| Text | `T` | Click and type; click outside or press Esc to finish. Font, size, bold, background |
| Step number | `N` | Each click places the next number |
| Blur, Pixelate | `B`, `X` | Hides parts of the screenshot; always drawn under the other annotations |
| Crop | `C` | Drag the area to keep, adjust it, then Enter (Esc cancels) |

The **Image** menu rotates the image left/right, resizes it, and adds a border or a drop shadow.

| Keys | Action |
|---|---|
| `Ctrl+Z` / `Ctrl+Y` | Undo / redo (every change, including crop and resize) |
| `Delete`, arrow keys | Delete or nudge the selection (Shift: 10 px) |
| `Ctrl+S` | Save. A capture that was already saved is overwritten in place; otherwise you are asked where |
| `Ctrl+Shift+S` | Save as (PNG, JPEG or BMP, chosen by the extension) |
| `Ctrl+C` | Copy the image with its annotations |
| `Ctrl+wheel`, `Ctrl+=` / `Ctrl+-` | Zoom at the mouse / in the centre |
| `Ctrl+0` / `Ctrl+1` | Fit in the window / 100% |
| Wheel, `Shift+wheel`, middle-drag, `Space`+drag | Pan |

Closing an editor (or exiting the app) with unsaved changes asks whether to save them.

## Layout

```
src/CashGear.Capture.Core       Models, interfaces, settings, pixel math (net10.0, no WPF/WinRT)
src/CashGear.Capture.Interop    All P/Invoke + COM declarations
src/CashGear.Capture.Engine     Capture engines (WGC, GDI), monitor/window enumeration
src/CashGear.Capture.Editor     Editor core: annotations, undo/redo, Skia rendering, zoom (net10.0, no WPF)
src/CashGear.Capture.Recording  Screen recording: WGC frames -> ffmpeg (H.264 MP4)
tools/get-ffmpeg.ps1            Downloads the pinned ffmpeg build into tools/ffmpeg (not committed)
src/CashGear.Capture.App        WPF app: tray, windows, view models, output
tests/                          xUnit v3 + AwesomeAssertions (Core, Editor, Engine, App)
```

## Files on disk

| What | Where |
|---|---|
| Settings | `%AppData%\CashGear\Capture\settings.json` |
| Logs | `%LocalAppData%\CashGear\Capture\logs\` |
| Captures (default) | `Pictures\CashGear Capture\` |

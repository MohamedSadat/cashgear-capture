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
| 8 | Capture Text: Windows OCR to the clipboard, English + Arabic in one area | done |
| 9 | Capture history, delayed capture with countdown, live light/dark, clickable notifications, installer + updates (Velopack) | done (fresh Windows 10/11 install pending) |

See [docs/CHANGELOG.md](docs/CHANGELOG.md) for what each phase delivered and [docs/TESTING.md](docs/TESTING.md) for the manual test matrix.

## Install

Run `CashGear.Capture-win-Setup.exe`. No administrator rights are needed: the app installs for the current user in `%LocalAppData%\CashGear.Capture`, adds Start menu and desktop shortcuts, and starts. Look for its icon in the notification area (under ^ if Windows hides it). It brings its own .NET runtime and ffmpeg. Remove it from Settings › Apps (Apps & Features); a running copy is closed first.

Until the build is signed, Windows SmartScreen may say "Windows protected your PC" for a downloaded Setup.exe (More info › Run anyway).

## Requirements (building)

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

### Installer, signing and updates

```powershell
powershell -File tools\package.ps1                       # version from Directory.Build.props
powershell -File tools\package.ps1 -Version 1.0.1 -UpdateFeedUrl https://downloads.example.com/capture
```

- **Output.** The script publishes the app self-contained (win-x64, single-file exe plus WPF's and SkiaSharp's native DLLs, `ffmpeg\`, `THIRD-PARTY-NOTICES.txt`). Velopack (`vpk`, a local dotnet tool pinned in `dotnet-tools.json`) then writes `artifactseleases\`: `CashGear.Capture-win-Setup.exe`, a portable zip, the full and delta `.nupkg` files and `releases.win.json`. Keep that folder between releases; deltas are made against the previous version in it.
- **Updates.** `-UpdateFeedUrl` builds in where installed copies look for updates: an https address or a shared folder holding the contents of `artifactseleases`. It can be overridden by `updates.feedUrl` in settings.json. Without one, the app doesn't check. Installed copies check a minute after start and then daily (Settings › General can turn that off or check now), download in the background, and offer tray › **Restart to Update**. A downloaded update is otherwise installed at the next start.
- **Signing.** Pass `-SignParams "/a /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p …"` (signtool arguments), `-SignTemplate "<any command with {{file}}>"` or `-AzureTrustedSignFile metadata.json`. A build server can set `CASHGEAR_SIGN_PARAMS`, `CASHGEAR_SIGN_TEMPLATE` or `CASHGEAR_AZURE_SIGN_FILE` instead. The app's binaries, the updater and Setup.exe are all signed.
- **Leaving ffmpeg out.** `-NoFfmpeg` packages without it; recording then explains what is missing. ffmpeg is a GPL build run as a separate program; its licence ships in `ffmpeg\LICENSE`.
- **Icon.** `tools\make-icon.ps1` redraws `src\CashGear.Capture.App\Assets\app.ico`.

The app starts hidden in the system tray. Command-line switches forward to the running instance:

| Switch | Effect |
|---|---|
| *(none)* | Start, or tell the running instance to show an "already running" notice |
| `--capture-fullscreen` | Capture every monitor plus the stitched virtual screen |
| `--capture-region` | Open the region overlay (drag a rectangle or click a window, Enter to capture, Esc to cancel) |
| `--capture-window` | Open the window picker (click a window to capture its own content) |
| `--capture-scrolling` | Open the window picker, then scroll the clicked window to the bottom and stitch one tall image |
| `--record` | Start a screen recording (pick the area), or stop the one running |
| `--capture-text` | Pick an area and copy the text in it (OCR) to the clipboard |
| `--exit` | Shut the running instance down |

## Hotkeys

| Default | Action |
|---|---|
| `PrintScreen` | Capture Region |
| `Alt+PrintScreen` | Capture the active window |
| `Ctrl+PrintScreen` | Capture Full Screen |
| `Ctrl+Shift+PrintScreen` | Scrolling capture |
| `Shift+PrintScreen` | Start a screen recording / stop it |
| `Ctrl+Alt+PrintScreen` | Capture Text (OCR) |

Change them in **Settings › Hotkeys** (click a box and press the keys) or in `settings.json` under `hotkeys`. A key another app or Windows already owns is reported, and a single typing key (e.g. `A`) is refused because it would stop working everywhere else.

## Settings

Tray › **Settings…** opens the settings window (light/dark follows Windows, or choose one):

| Tab | What |
|---|---|
| Output | After capture: open in the editor / save to file / copy to clipboard (any combination); folder; PNG/JPEG/BMP and JPEG quality; file-name template with a live example |
| Capture | Capture delay (also in the tray), hide delay, cursor, window shadow, full-screen output, scrolling capture (wait after each scroll, height limit), capture method |
| Recording | Frames per second (15/30/60), quality, encoder (hardware when available, or x264), pointer, click highlights, where ffmpeg is |
| Hotkeys | Record hotkeys; each shows whether it is active |
| General | Start with Windows, text recognition language (Automatic or one installed language), theme (follows Windows live), how many captures the history keeps, version and updates, open the settings file |

Everything is stored in `%AppData%\CashGear\Capture\settings.json`; edits to the file apply as soon as it is saved.

File-name tokens: `{yyyy} {MM} {dd} {HH} {mm} {ss}` (capture time), `{counter}` (0001, 0002, … kept across restarts), `{kind}` (fullscreen, monitor, region, window, scrolling, recording), `{monitor}` (e.g. DISPLAY1), `{window}` (window title). Default: `Capture_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}_{kind}`.

## Delayed capture

Tray › **Capture Delay** › 3, 5 or 10 seconds (Settings › Capture has more). Every capture then counts down in a small box in the bottom-right corner first. Open the menu or hover the tooltip you want in the shot meanwhile. The box doesn't take the focus, so they stay open, and it is never in the capture. **Cancel** stops the capture. The menu shows the current delay; choose "No delay" to turn it off.

## Capture history

The last 20 captures are kept (Settings › General: off to 100), even ones that were only copied or edited.

- Tray › **Recent Captures** shows the newest with thumbnails; click one to open it in the editor.
- Tray › **Capture History…** shows them all: Open in Editor (double-click or Enter), Copy (Ctrl+C), Show in Folder, Save As…, Remove (Delete) and Clear History.
- The copies live in `%LocalAppData%\CashGear\Capture\history`; removing them never touches the files you saved.

Clicking a "Capture saved" or "Recording saved" notification shows the file in Explorer.

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

## Capture Text

Tray › **Capture Text** (or `Ctrl+Alt+PrintScreen`): drag around the text, or click a window, and press Enter. The text is read with Windows OCR and copied to the clipboard. A notification shows the start of the text and the language used. Nothing is saved, and the image isn't opened in the editor.

- **Automatic** (the default) reads the area once with each of your Windows languages that has OCR, and takes every line from the language that matches its script. So English and Arabic lines in the same area both come out right. Arabic is given in reading order (right to left).
- To choose one language, use **Settings › General**. The list shows the OCR languages Windows has. To add one, go to Windows Settings › Time & language › Language, choose the language › Options › Optical character recognition.
- Lines are separated by line breaks, and paragraphs by a blank line. Leave a small margin around the text, and keep text cursors away from it: anything that touches a word can spoil it.

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
tools/package.ps1               Builds the installer and update packages (Velopack) into artifacts/
tools/make-icon.ps1             Draws the app icon
src/CashGear.Capture.App        WPF app: tray, windows, view models, output
tests/                          xUnit v3 + AwesomeAssertions (Core, Editor, Engine, App)
```

## Files on disk

| What | Where |
|---|---|
| Settings | `%AppData%\CashGear\Capture\settings.json` |
| Logs | `%LocalAppData%\CashGear\Capture\logs\` |
| Captures (default) | `Pictures\CashGear Capture\` |
| Capture history | `%LocalAppData%\CashGear\Capture\history\` |
| Installed app (Setup.exe) | `%LocalAppData%\CashGear.Capture\` (removed by uninstall; the rows above are kept) |

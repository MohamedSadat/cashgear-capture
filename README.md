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
| — | Sound in screen recordings: system audio and a microphone, AAC, kept in sync through pauses | done |
| 10 | Recording engine v2: FFmpeg in-process (LGPL), timestamped frames, crash-safe temp file + recovery, encoder detection and fallback, sound tracks per source | done |
| 11 | Pro audio: devices followed during a recording, one app's sound (or all but one), microphone noise gate / compressor / limiter and RNNoise, level meters, microphone test, loudness to −16 LUFS, push-to-talk | done (RNNoise build and manual sound checks pending) |
| 12 | Recording overlays: clicks, pointer effects, keystrokes (never from password fields), webcam, drawing on the screen; drawn on the GPU or added later from a metadata track | done (real webcam and browser checks pending) |

See [docs/CHANGELOG.md](docs/CHANGELOG.md) for what each phase delivered and [docs/TESTING.md](docs/TESTING.md) for the manual test matrix.

## Install

Run `CashGear.Capture-win-Setup.exe`. No administrator rights are needed: the app installs for the current user in `%LocalAppData%\CashGear.Capture`, adds Start menu and desktop shortcuts, and starts. Look for its icon in the notification area (under ^ if Windows hides it). It brings its own .NET runtime and FFmpeg. Remove it from Settings › Apps (Apps & Features); a running copy is closed first.

Until the build is signed, Windows SmartScreen may say "Windows protected your PC" for a downloaded Setup.exe (More info › Run anyway).

## Requirements (building)

- Windows 10 2004 (build 19041) or later, x64
- .NET SDK 10.0.401+ (pinned in `global.json`)
- For screen recording: FFmpeg's libraries. Run `tools\get-ffmpeg.ps1` once (it downloads a pinned, checksum-verified LGPL build into the git-ignored `tools\ffmpeg`); the build copies the six libraries the app uses next to it as `ffmpeg\`. Without them everything else works and recording says what is missing. See [docs/LICENSES.md](docs/LICENSES.md).
- For microphone noise suppression: RNNoise. Run `tools\get-rnnoise.ps1` once; it needs Visual Studio's C++ compiler ("Desktop development with C++"). It builds `rnnoise.dll` from the pinned, checksum-verified RNNoise 0.2 source into the git-ignored `tools\rnnoise`, and the build copies it next to the app as `rnnoise\`. Without it Settings doesn't offer noise suppression.

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

- **Output.** The script publishes the app self-contained (win-x64, single-file exe plus WPF's and SkiaSharp's native DLLs, `ffmpeg\`, `rnnoise\`, `THIRD-PARTY-NOTICES.txt`). Velopack (`vpk`, a local dotnet tool pinned in `dotnet-tools.json`) then writes `artifacts\releases\`: `CashGear.Capture-win-Setup.exe`, a portable zip, the full and delta `.nupkg` files and `releases.win.json`. Keep that folder between releases; deltas are made against the previous version in it.
- **Updates.** `-UpdateFeedUrl` builds in where installed copies look for updates: an https address or a shared folder holding the contents of `artifacts\releases`. It can be overridden by `updates.feedUrl` in settings.json. Without one, the app doesn't check. Installed copies check a minute after start and then daily (Settings › General can turn that off or check now), download in the background, and offer tray › **Restart to Update**. A downloaded update is otherwise installed at the next start.
- **Signing.** Pass `-SignParams "/a /fd sha256 /tr http://timestamp.digicert.com /td sha256 /f cert.pfx /p …"` (signtool arguments), `-SignTemplate "<any command with {{file}}>"` or `-AzureTrustedSignFile metadata.json`. A build server can set `CASHGEAR_SIGN_PARAMS`, `CASHGEAR_SIGN_TEMPLATE` or `CASHGEAR_AZURE_SIGN_FILE` instead. The app's binaries, the updater and Setup.exe are all signed.
- **Leaving FFmpeg out.** `-NoFfmpeg` packages without it; recording then explains what is missing. FFmpeg is an LGPL v3 build loaded as separate DLLs (replaceable); its licence texts ship in `ffmpeg\`, and [docs/LICENSES.md](docs/LICENSES.md) says where its source is.
- **RNNoise.** The script builds it with `get-rnnoise.ps1` if `tools\rnnoise` doesn't have it yet; `-NoRnnoise` packages without it (no noise suppression).
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
| `Ctrl+Shift+D` | Draw on the screen (only while recording; the key is free otherwise) |

Change them in **Settings › Hotkeys** (click a box and press the keys) or in `settings.json` under `hotkeys`. A key another app or Windows already owns is reported, and a single typing key (e.g. `A`) is refused because it would stop working everywhere else.

## Settings

Tray › **Settings…** opens the settings window (light/dark follows Windows, or choose one):

| Tab | What |
|---|---|
| Output | After capture: open in the editor / save to file / copy to clipboard (any combination); folder; PNG/JPEG/BMP and JPEG quality; file-name template with a live example |
| Capture | Capture delay (also in the tray), hide delay, cursor, window shadow, full-screen output, scrolling capture (wait after each scroll, height limit), capture method |
| Recording | Frames per second (15/30/60), quality (Small / Balanced / High / Near lossless), encoder (Automatic, Software only, or any encoder, each marked with whether it works here; Check again), pointer. Sound: system audio on/off, all sound / only one app / all except one app, the output device (the default, followed, or one by name), the microphone (none, the Windows default or a connected one); with both, one mixed track, a track per source, or both. Microphone processing (noise suppression with its strength, noise gate with its threshold, compressor, limiter), live level meters, the microphone test, evening out loudness when saving. FFmpeg's version |
| Overlays | Draw the overlays into the video or add them later; the metadata file; clicks (colours, size, sound); pointer (halo or spotlight, size, hide when idle); keystrokes (shortcuts or all keys, position, font, size, time, apps never shown); webcam (camera, picture, shape, size, border, mirror, placement preview); drawing (pen colour, width, fade) |
| Hotkeys | Record hotkeys; each shows whether it is active. Push to talk |
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
- **Encoding** happens inside the app (FFmpeg). Which encoders work is found out once (Settings › Recording shows the result, and **Check again** repeats it after a driver update): the graphics card's own encoder (NVIDIA, Intel, AMD), then the card's encoder through Windows (Media Foundation), then Windows' software encoder, then OpenH264. If the encoder stops working in the middle, the next one takes over and the rest is saved as a second file, "name (part 2).mp4".
- **Timing.** Every picture and every sound packet is stamped with the time Windows captured it, on one clock, so they stay together however long the recording runs. The video has a constant frame rate; when the screen is still, the same picture repeats. If encoding falls far behind, frames are skipped (the picture holds still for a moment) but the sound never is.
- **Crash-safe.** While recording, the video is written to a temporary file in `%LocalAppData%\CashGear\Capture\Recordings\Temp` that survives the app being closed or the PC losing power, losing about a second. Stop turns it into the MP4. A recording left behind is found when the app starts: a notification, and tray › **Save Interrupted Recordings**, save it where it was going.
- **Sound.** System audio (what you hear on the default output device) is recorded by default. A microphone can be added in Settings › Recording. Both are saved in the MP4 as AAC (48 kHz stereo): mixed into one track, or (Settings) a track per source for editing, or both.
  - **One app.** System audio can be one app's sound only, or all sound except one app's (a call, music), whichever device it plays to. The app is found when the recording starts, and followed if it starts or restarts later.
  - **Devices.** The default output device and microphone are followed when Windows' default changes. A device unplugged during a recording leaves silence until it is back, and is then recorded again; notifications say so, and the recording goes on.
  - **The microphone** can be cleaned up as it is recorded: noise suppression (RNNoise) for fans, hum and typing; a noise gate between words; a compressor; and a limiter that keeps it below −1 dB (on by default). Settings › Recording shows live meters and has a microphone test: record 5 seconds and play them back as recorded or processed.
  - **The toolbar** shows a level meter per source (red at the right end when it clipped) and, with a microphone, a **Mute mic** button. Its sound button opens volume (0–200%), mute, a wider meter for each source and the noise suppression switch; the volumes are kept for the next recording.
  - **Push-to-talk** (Settings › Hotkeys): with a key set, the microphone is only recorded while the key is held.
  - **Loudness.** With Settings › Recording › Even out the loudness, every recording is saved at −16 LUFS (the sound is measured and encoded again; the video isn't).
  - Sound is kept on the recording's clock: silences stay silent, Pause pauses the sound too, and the sound and picture end together.
  - If no sound device is available, the video is recorded without sound and the notification says why. With system audio off and no microphone, the MP4 has no sound track.

## Recording overlays

Settings › **Overlays** chooses what recordings show of what you do. Everything is drawn on the graphics card before the video is encoded, never with windows on the screen, so it is exactly on the frame where it happened.

- **Clicks**: an expanding ring at each press (yellow for left, red for right, green for middle by default), and a dot following a held button. Optionally a short click sound in the recording's sound.
- **Pointer**: a halo or a spotlight (the rest dimmed), a bigger pointer (up to 3×), and hiding it when it stands still. For the last two the app draws the pointer itself.
- **Keystrokes** (off by default): shortcuts such as `Ctrl + Shift + S` (repeats as `×3`), or all keys including typing, in a box at the bottom (or another corner), fading after the last key.
  - **Privacy.** Keys typed into a password field are never shown or kept (Windows' UI Automation says which fields are passwords; browsers tell it). Neither are keys typed into the apps listed in Settings (password managers by default). When the check can't tell, keys are hidden.
  - **No key log.** Keys go nowhere but the recording and its metadata file. The keyboard is only followed while recording with keystrokes on.
  - **The toolbar** has a keyboard button to hide keystrokes for a while.
- **Webcam**: a circle or rounded rectangle with a border, dragged into place in Settings' preview. It is also saved as a second video track in the MP4 ("Webcam"), so an editor can move or resize it later. Every webcam picture keeps the time it was captured, so it stays in time with the sound however long the recording.
- **Drawing on the screen**: while recording, `Ctrl+Shift+D` or the pen on the toolbar lets you draw over the recorded area with a pen, highlighter, arrow or rectangle. The drawing is in the video, the toolbar isn't.
  - **Keys:** Esc stops drawing (the drawings stay), Delete clears them, Ctrl+Z undoes, 1–4 pick a tool.
  - **Fading:** drawings can fade away by themselves after a set time.
- **Metadata file**: next to each recording, `name.cgmeta.json` keeps the pointer on every frame, the clicks, the keys (only as the keystroke overlay showed them), which app was in front, and when you drew. All times are on the video's clock.
- **Adding overlays later**: with "Keep the video clean" in Settings, nothing is drawn while recording. Tray › **Add Overlays to a Recording…** then writes `name (overlays).mp4` with the overlays as Settings has them now, from the metadata file. The recording itself isn't changed.

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
src/CashGear.Capture.Media      FFmpeg in-process (FFmpeg.AutoGen): encoders, crash-safe writer, remux, loudness, decoding (net10.0)
src/CashGear.Capture.Audio      Sound devices: WASAPI capture with Windows' timestamps, devices followed, process loopback, microphone chain, RNNoise, meters
src/CashGear.Capture.Overlays   Recording overlays: input hooks + keystroke privacy (UI Automation), pointer shapes, Direct2D compositor, webcam (Windows.Media.Capture)
src/CashGear.Capture.Recording  Screen recording: timestamped WGC pictures + sound -> overlays on the GPU -> encoders -> temp file -> MP4; webcam track; recovery; adding overlays later
tools/get-ffmpeg.ps1            Downloads the pinned LGPL FFmpeg build into tools/ffmpeg (not committed)
tools/get-rnnoise.ps1           Builds rnnoise.dll from the pinned RNNoise source into tools/rnnoise (not committed; needs MSVC)
tools/SyncTest                  Recording acceptance test: flashes + beeps, then sync, drift, CPU (docs/TESTING.md)
tools/licenses                  License texts shipped with FFmpeg
tools/package.ps1               Builds the installer and update packages (Velopack) into artifacts/
tools/make-icon.ps1             Draws the app icon
src/CashGear.Capture.App        WPF app: tray, windows, view models, output
tests/                          xUnit v3 + AwesomeAssertions (Core, Editor, Engine, Media, Audio, Overlays, App)
```

## Files on disk

| What | Where |
|---|---|
| Settings | `%AppData%\CashGear\Capture\settings.json` |
| Logs | `%LocalAppData%\CashGear\Capture\logs\` |
| Captures (default) | `Pictures\CashGear Capture\` |
| Capture history | `%LocalAppData%\CashGear\Capture\history\` |
| Recordings in progress (and ones a crash left) | `%LocalAppData%\CashGear\Capture\Recordings\Temp\` |
| Which encoders work here | `%LocalAppData%\CashGear\Capture\encoders.json` |
| A recording's metadata (pointer, clicks, keys) | `name.cgmeta.json` next to the recording |
| Installed app (Setup.exe) | `%LocalAppData%\CashGear.Capture\` (removed by uninstall; the rows above are kept) |

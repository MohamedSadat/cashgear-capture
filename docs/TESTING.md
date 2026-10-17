# Testing

## Automated

```powershell
dotnet test --solution CashGear.Capture.sln
```

Tests tagged `Category=Interactive` need a logged-in, unlocked desktop. They skip themselves when none is available or when `CASHGEAR_SKIP_INTERACTIVE=1`. On Windows 10 the WGC tests flash a yellow border around each monitor — expected.

The recording and Media tests use the real FFmpeg libraries from `tools\ffmpeg` (run `tools\get-ffmpeg.ps1`), plus that build's `ffmpeg.exe` and `ffprobe.exe` to check files independently, and skip without them. Encoder detection for the recorder tests is cached in `%TEMP%\cashgear-tests\encoders`. Tests that capture the screen run one at a time (a Windows 10 capture border would otherwise show up in another test's pixels); the region-overlay pixel test can also fail while another app draws over the screen (for example a "using your computer" indicator).

The WGC leak soak test is explicit:

```powershell
dotnet test --project tests/CashGear.Capture.Engine.Tests -- --explicit only
```

So is the real-sound recording test, because it plays quiet beeps through the default output device for about 10 s:

```powershell
dotnet test --project tests/CashGear.Capture.App.Tests -- --explicit only
```

The other recording tests with sound use a generated tone (no sound is played) or real system audio with whatever is playing.

### Recording acceptance: `tools\SyncTest`

SyncTest records the primary monitor with the real recorder while a 240 px square in its top-left corner turns white whenever a 50 ms 1 kHz beep starts on the default output device (every 2 s). The beeps are placed by the sound card's clock and each flash is timed from that clock read against QPC, so the stimulus itself can't drift. It then decodes the file and pairs each flash with its beep. The report gives the constant offset (the screen shows the flash a frame or so after the beep is heard: display delay, not recorder error), its spread (whole frames, at 30 fps ±33 ms) and the drift (a fitted line, and the first versus the last 5 minutes), plus the process's CPU while recording. Results go to `artifacts\synctest` (`.report.txt`, `.offsets.csv` per file). Keep other sound off during a run: the beep detector takes 30% of the track's loudest sound as its threshold.

```powershell
$sync = "tools\SyncTest\bin\Release\net10.0-windows10.0.22621.0\CashGear.Capture.SyncTest.exe"
dotnet build tools\SyncTest -c Release
& $sync run --minutes 60 --pauses 3                    # drift over an hour (auto encoder), 3 pauses of 5 s
& $sync run --minutes 5 --busy --encoder h264_mf_hw    # CPU: a square repaints every frame, so every picture is read back
& $sync run --seconds 600 --out artifacts\synctest\k1  # then Stop-Process -Force it, and:
& $sync recover --out artifacts\synctest\k1           # saves what the killed run left, and analyzes it
& $sync analyze <file.mp4>
```

Other options: `--fps 15|30|60`, `--encoder auto|software|<id>`, `--quality Small|Balanced|High|Lossless`, `--microphone default|<id>`, `--tracks Mixed|Separate|Both`, `--interval <s>`.

## Manual test matrix

| Configuration | Status |
|---|---|
| Single monitor 100% | not run |
| Single monitor 150% | not run |
| Dual monitor 100% + 100% (secondary right of primary, offset Y) | Phase 0–9 and sound in recordings pass (2026-10-15) |
| Dual monitor, mixed DPI (100% + 150%) | **pending** — see "Pending" below (covers Phases 1–2) |
| Monitor left of primary (negative X) | **pending** — see "Pending" below (covers Phases 1–2) |
| Windows 10 22H2 | Phase 0–9 and sound in recordings pass on the development PC (2026-10-15); Phase 10 passes (2026-10-17); fresh install **pending** |
| Windows 11 23H2+ | not run (install steps under Phase 9 › Pending) |

## Phase 0–1 results (2026-10-14)

Machine: Windows 10 Pro 22H2 (19045); `\\.\DISPLAY2` primary 1920×1080 at (0,0), `\\.\DISPLAY1` 1280×1024 at (1920,58), both 100%; virtual screen (0,0) 3200×1082.

| Check | Result |
|---|---|
| Launch → single process, tray icon, log written, PerMonitorV2 logged | pass |
| Second launch exits 0 and forwards `activate` | pass |
| `--exit` / tray Exit → process gone < 1 s, "Shutdown complete", no ghost tray icon | pass |
| Tray menu shows Capture Region / Capture Window / Capture Full Screen / Settings… / Exit | pass |
| Capture Full Screen (tray and `--capture-fullscreen`), Auto backend → GDI | pass |
| `_all.png` 3200×1082, gaps (2500,10) and (100,1081) alpha 0, content alpha 255 | pass |
| `DISPLAY2-primary.png` 1920×1080, `DISPLAY1.png` 1280×1024, crops equal the stitched image | pass |
| Tray menu absent from the capture taken from it | pass (hide delay; display affinity fails on the popup) |
| Clipboard has Bitmap, DIB and PNG; paste into Paint → 3200×1082 | pass |
| `capture.backend = Wgc` → captured via WGC, same dimensions | pass |
| `capture.wgcFrameTimeoutMs = 1` → WGC timeout logged per monitor, GDI fallback | pass |

## Phase 2 results (2026-10-14)

Same machine and layout as above. The overlay is excluded from capture, so it is invisible in screenshots and screen recordings; the checks below use the app's debug log and the saved files.

| Check | Result |
|---|---|
| Overlay test (`RegionOverlayWindowTests`): window rect = virtual screen (0,0 3200×1082); screen→canvas mapping exact on both monitors; frozen image shown 1:1 (excluding taskbars while the test runner is not foreground) | pass, 3 runs |
| Overlay opened from the tray: activated, covers 3200×1082; a click on the taskbar strip at (1358,1059) reaches the overlay | pass |
| Esc cancels; no file written | pass |
| Click on maximized Notepad → `_window.png` 1920×1040 = its DWM frame (0,0)-(1920,1040) | pass (after skipping click-through windows) |
| Drag (13,40)→(527,132), Right ×2, Shift+Down → (15,40 515×94), file 515×94 | pass |
| Region pixels vs the same rect of the window capture: text-pixel IoU 100% aligned, 21–31% at ±1 px | pass |

Note: while Claude's computer-use is active, its "Claude is using your computer" indicator draws an animated, click-through, full-screen glow. That is what made the desktop look "animated" in the Phase 1 WGC tests, and why exact colour comparisons between two live grabs don't hold during such sessions.

## Phase 3 results (2026-10-14)

Same machine and layout. The keys were sent with `SendInput` from a script; the screen-control tool can't press PrintScreen, and the OS delivers injected keys to `RegisterHotKey` exactly like physical ones.

| Check | Result |
|---|---|
| Startup: 4 default hotkeys registered, 0 problems | pass |
| Alt+PrintScreen with Notepad in front → `_window.png` 1426×575 = Notepad's frame (325,487)-(1751,1062), clean content | pass (WGC, because the computer-use indicator counts as covering) |
| Ctrl+PrintScreen → `_all` 3200×1082 + both monitors | pass |
| PrintScreen → overlay opens activated; Esc → "Region selection cancelled" | pass |
| Ctrl+Shift+PrintScreen → scrolling action (Phase 6 notice) | pass |
| Live edit of settings.json → reloaded and re-registered within ~1 s | pass |
| Live edit `captureRegion: "Win+E"` → Win32 error 1409 logged, "3 registered, 1 problem" + notification, no crash | pass |
| `--capture-window` → window-only picker, one click on Notepad → WGC capture 1426×575 | pass |
| Interactive tests: hotkey round trip via SendInput; conflict between two registrations; WGC through a covering window | pass |

## Phase 4 results (2026-10-15)

| Check | Result |
|---|---|
| Existing v1 settings.json → "Upgraded settings … from schema 1 to 2", file rewritten (readable `+`, no computed fields) | pass |
| Tray › Settings… opens the window (Fluent light); tray menu shows the active hotkeys | pass |
| Folder, JPEG, template `Shot_{counter}_{kind}`, Full Screen hotkey Ctrl+Shift+F9, Start with Windows, Dark → Apply: settings.json updated, hotkeys re-registered, Run key written | pass |
| PrintScreen pressed in the Region hotkey box is recorded (hotkeys paused), no screenshot taken | pass |
| Dark theme applied live with dark title bar and readable checkboxes | pass (after fixing a style that froze light colours) |
| Restart → Ctrl+Shift+F9 → `Shot_0001_fullscreen_all.jpg` 3200×1082, `Shot_0002_monitor_DISPLAY2.jpg`, `Shot_0003_monitor_DISPLAY1.jpg` in the chosen folder | pass |
| Settings reopened after restart shows the saved values | pass |
| Ctrl+PrintScreen with Settings open: the capture shows what is behind the window; the window reappears | pass |
| Reset to defaults + Start with Windows off + OK → defaults saved, Run key removed | pass |

## Phase 5 results (2026-10-15)

| Check | Result |
|---|---|
| `--capture-fullscreen` with Open in the editor + Save → the editor opens on the 3200×1082 image, titled with the saved file name, fitted at 44.6%; the gap between monitors shows as a checkerboard | pass |
| Arrow, rectangle, ellipse, text, step numbers 1–2, blur, highlight, pixelate and pen, picked with the toolbar keys (A, R, E, T, N, B, H, X, D) | pass |
| Select tool: move a rectangle, Ctrl+Z restores it; panel shows the selected annotation's style | pass |
| 100% button / Ctrl+1, Ctrl+wheel (real input: 44.6% → 66.7% at the mouse), Fit / Ctrl+0; pan with wheel, middle-drag, Space+drag (no shape drawn) | pass |
| Crop: frame with dimmed surroundings and size label (1186 × 711), Enter applies and returns to Select | pass |
| Image menu: rotate right (711 × 1186), add shadow (1236 × 761), resize to 593 × 356 (height follows width), blue 4 px border (1194 × 719); each undone | pass |
| Ctrl+S overwrites the auto-saved PNG with the edited 1186 × 711 image (annotations present); no temporary file left | pass |
| Save As `edited_copy.jpg` → valid JPEG, title follows | pass |
| Ctrl+C → clipboard has the 1186 × 711 image as Bitmap, DIB and PNG | pass |
| Text: two lines typed in place, committed by clicking away; double-click with Select edits it | pass |
| Region capture (PrintScreen, drag, Enter) opens a 726 × 463 editor | pass |
| Blur drawn over a rectangle leaves the rectangle visible | pass (after fixing: blur was drawn over it) |
| Closing with changes asks; Cancel keeps the window. `--exit` with changes asks; No discards and the app exits | pass |
| A second capture with an editor open: the open editor isn't in the shot, the new editor opens in front and active | pass (after fixing: the old editor came back on top) |

## Phase 6 results (2026-10-15)

Test documents: 240 bars whose lengths repeat 1–40 units, so the order of bar lengths in the result shows any duplicated or missing line. A script measures every bar in the stitched image.

| Check | Result |
|---|---|
| Notepad, 240-bar document, picked on the overlay: UI Automation "Text Editor" document found, scrolled 342 px per step, 25 frames, ReachedEnd | pass |
| Notepad result 868 × 8,664: 240 bars in order (1…40), all 18 rows, spacing 36 rows ×239; scrollbar (16 px) cut off | pass |
| Chrome guest window, 240-bar page with a sticky header: no UI Automation scroll element → client area + mouse wheel; 1 notch measured 100 px, then 400 px steps; 14 frames, ReachedEnd | pass |
| Chrome result 971 × 5,423: 240 bars in order, all 12 rows, spacing 22 rows ×239; browser toolbar and sticky header once at the top | pass |
| Progress window visible during the capture, display affinity 0x11 (excluded from capture) | pass |
| Esc during a capture: "cancelled by the user", no file written, progress window closed, pointer restored | pass |

## Phase 7 results (2026-10-15)

ffmpeg 9.0.2 (Gyan essentials, SHA-256 checked). Encoder probe on this PC: NVENC no (driver 457.51: "Cannot load cuMemAllocAsync"), Quick Sync no, AMF no, x264 yes.

| Check | Result |
|---|---|
| `--record`, drag (0,0)–(1919,1079), Enter → recording DISPLAY2 1920×1080 at 30 fps with libx264, quality 23 | pass |
| Toolbar visible at the top centre during recording, display affinity 0x11 (excluded from capture), not in the video | pass |
| 60 s with a full-screen animation changing every frame: 1,853 frames written, 0 dropped (0.0%), 1 repeated | pass (≤ 5% drops required) |
| ffprobe: h264 High, 1920×1080, r_frame_rate = avg_frame_rate = 30/1, 1,853 frames, 61.8 s, 2.0 Mbit/s, 15.8 MB | pass |
| App memory every 5 s: private 212–239 MB, working set 236–259 MB, no growth | pass (flat) |
| Region recording with three scripted clicks: yellow highlight under the pointer just after a click; pointer recorded | pass |
| Toolbar Stop saved the recording (7.1 s, 214 frames, 0 dropped) and the notification appeared | pass |
| `--exit` while idle: app and ffmpeg gone | pass |

## Phase 8 results (2026-10-15)

- OCR languages installed in Windows: `ar-SA`, `de-DE`, `en-US`.
- Display languages: en-US, ar-EG, de-DE. Automatic therefore reads with en-US, ar-SA and de-DE.

The sample was a Notepad window (default font, 100% scaling) with two English lines, a blank line and two Arabic lines. The OCR language was Automatic. Each check ran `--capture-text`, dragged the area, pressed Enter, then read the clipboard.

| Check | Result |
|---|---|
| Hotkeys at startup: `Ctrl+Alt+PrintScreen` registered for CaptureText, 6 registered, 0 problems | pass |
| English area (2 lines): text matches exactly, 103 characters | pass |
| Arabic area (2 lines): words in reading order. Line 1 exact; line 2 has one letter wrong ("إعلاق" for "إغلاق") | pass (readable) |
| English + Arabic area: English exact, blank line, Arabic as above, 182 characters. English lines from en-US, Arabic lines from ar-SA | pass (readable) |
| Notepad's blinking cursor next to a word garbles that word (cursor after "Monday." gave "I%nday.l"). An area whose edge cut 1 px into the leftmost Arabic word garbled that word. Both were exact with the cursor moved away and a small margin. | noted (Known limitations) |
| Settings unchanged; app exited with `--exit` | pass |

## Phase 9 results (2026-10-15)

Installer built with `tools\package.ps1`: Setup.exe 118.5 MB, full package 111.3 MB, delta 1.0.0 → 1.0.1 under 0.1 MB, unsigned. The update feed was a local folder (`-UpdateFeedUrl`). This is the development PC, not a fresh machine; the app is self-contained, so the .NET installed here isn't used.

| Check | Result |
|---|---|
| Setup.exe (no admin prompt): `%LocalAppData%\CashGear.Capture\current\`, Start menu + desktop shortcuts, Apps & Features "CashGear Capture 1.0.0 / CashGear", app started (log: "first run after install") | pass |
| Tray: new icon, Capture Delay ▸ (No delay ✓ / 3 / 5 / 10 s), Recent Captures (disabled while empty), Capture History…; no update item | pass |
| Capture Delay › 3 seconds → settings.json `delaySeconds: 3`, menu reads "Capture Delay (3 s)" | pass |
| Countdown (sampled every 250 ms): bottom-right of the primary work area, 16 px margins; 3 → 2 → 1; display affinity 0x11 (excluded from capture); WS_EX_NOACTIVATE; never the foreground window; closed before the capture | pass |
| Cancel on the countdown: "Capture cancelled", no files | pass |
| A second `--capture-fullscreen` during the countdown: "A capture is already in progress; the new request was ignored" | pass |
| Recent Captures: 4 captures with thumbnails and "Today 11:34 AM · Full screen · 3200 × 1082" | pass |
| Capture History window: 4 thumbnails, buttons; Open in Editor opened the capture with its saved file | pass |
| Settings › General: Capture history (20 captures), Updates: "Version 1.0.0", automatic checks on, "You have the latest version (1.0.0)" | pass |
| 1.0.1 packed into the feed; Check now → "Update 1.0.1 downloaded" (delta), "Version 1.0.1 is ready"; tray shows "Restart to Update (1.0.1)" | pass |
| Restart to Update: 1.0.0 shut down cleanly, Velopack applied the update, 1.0.1 started ("updated to 1.0.1"); Apps & Features shows 1.0.1; no update item afterwards | pass |
| Start with Windows (Settings): Run value `"…\CashGear.Capture\current\CashGear.Capture.App.exe"`; with the app closed, running that command started it | pass (sign-in itself not tried) |
| Uninstall from Apps & Features' command with the app running: the hook asked it to exit (gone in 0.1 s), no process left, install folder, shortcuts, Apps & Features entry and Run value removed; settings kept | pass (failed before the hook fix: Velopack didn't see the running app) |
| Clicking "Capture saved" (development build) opened the capture folder in Explorer | pass |
| Installer built from main and run by hand: the app started without a tray icon ("TryCreate failed": the development build had claimed the shared icon id) | fail, fixed: per-path icon id; reinstalled with Setup, icon created on the first attempt, second launch handed off |
| The new file selected in that Explorer window | not in this OneDrive folder (see Known limitations) |
| Theme following a Windows light/dark switch | unit tests only (this PC's theme wasn't changed) |

Notes:
- While a screen-control session is active, Windows reports the user as "busy" (full screen), so notification banners go to the Action Center without showing.
- The countdown and the region overlay are excluded from screen capture, so they can't be checked from screenshots; the countdown was checked through its window and UI Automation instead.

## Sound in recordings results (2026-10-15)

Same PC: Windows 10 22H2, two monitors at 100%, x264 (no hardware encoder works here). The default output device is "Headphones (H5)" and the microphone "Microphone (H5)", both from a ZOOM H5 over USB, with Windows mixing at 48 kHz float. The development build ran with the installed copy closed, and the recordings were driven by `--record` and a scripted drag on the overlay. The toolbar was driven through UI Automation (it is excluded from screen capture, so screenshots can't show it). The beeps were a quiet 50 ms 1 kHz tone at the start of every second, played with `SoundPlayer`. Durations come from ffprobe; beep onsets from the decoded sound.

| Check | Result |
|---|---|
| Settings › Recording (rendered off-screen, light and dark): Sound section with "Record system audio (what you hear)", Microphone list (None, Default microphone, Microphone (H5)), "System audio is what plays on Headphones (H5)…" | pass |
| Toolbar with system sound + microphone: speaker, level meter, Mute mic, Pause, Stop; without a sound device: crossed-out speaker with the reason as tooltip | pass |
| System audio, 640×480 region, 10 s with a 1.35 s pause from the toolbar: h264 10.700 s (321 frames) and AAC LC 48 kHz stereo 10.700 s; 513,600 sound frames = 321 × 1,600; beeps 1.000 s apart, and 0.650 s across the pause (2 s less the 1.345 s pause) | pass |
| ffmpeg opened the sound pipe 50–62 ms after the clock started; Stop took about 150 ms (video writer, sound written and pipe closed, ffmpeg exit 0) | pass |
| System audio + H5 microphone (by device ID in settings.json, applied live), 8 s: toolbar showed **Mute mic**; muted from 3.0 s to 5.25 s. Room noise from the microphone at −45 to −55 dBFS, exact silence between beeps while muted, video and sound both 8.467 s | pass |
| System audio off, no microphone: only the h264 stream, as before | pass |
| 60 s of the whole primary monitor (1920×1080) with system audio: 1,814 frames, 0 dropped, constant 30 fps, video and sound both 60.467 s, 20 beeps exactly 1.000 s apart | pass |
| Explicit test `Real_sound_played_while_recording_lands_where_it_was_played` (10 s, pause, beeps through the default device): beep spacing kept within 30 ms across the pause | before the fix below, pass in 20 of about 21 runs (one froze at Stop); after it, 50 of 50 |
| Settings file restored from a backup, test recordings sent to the Recycle Bin, installed copy started again | done |

Notes on the explicit real-sound test:
- Twelve consecutive runs with logging (`CASHGEAR_TEST_LOG`) all passed with 481,600 sound frames (301 × 1,600), and no late, overflowed or skipped frames.
- Before that, one run found a beep captured during the pause just after Resume. It was a packet delivered late and placed at its arrival time; fixed by keeping Windows' timestamp.
- Another run hung before the test had a timeout. It looked like a hang at the start (ffmpeg idle, an empty file), but it was Stop freezing. A looped run froze the same way on 2026-10-15, with logs:
  - `WgcAreaStream.Dispose` released the capture while holding the stream's lock. When Windows delivered a frame at that moment, the frame's callback waited for the lock, and releasing the staging texture (inside the lock) waited for the callback.
  - Stop froze there, before it closed ffmpeg's video input and before any of its 60 s timeouts, so nothing ended it. ffmpeg waited for more video and held back the last 0.5 s of sound, so the sound writer waited in `WaitForPipeDrain`.
  - The freeze didn't depend on sound: a recording without sound could freeze at Stop the same way.
  - The empty file was misleading. Windows can list a stale size for a file another process has open: the looped run's file stayed listed at 48 bytes for its whole freeze. Once the test host was killed, ffmpeg finished the file normally.
  - Fixed: `Dispose` only marks the stream disposed under the lock (a frame arriving after that is ignored) and releases everything outside it. Stop also closes ffmpeg's video input before releasing the capture.
  - `Releasing_the_capture_while_a_frame_waits_returns` reproduces the freeze on demand. It holds the stream's lock until the release, and then a frame's callback, are both waiting for it. The old code froze in 3 of 3 runs; the fix passed 5 of 5.
  - After the fix, 50 looped runs of the explicit test all passed, the longest in 16.6 s. All had 481,600 sound frames; in two, under 2 ms of sound arrived late.
- Runs fail on beep timing while something else plays on the default output device (a decoded peak of 0.0147 instead of the beeps' 0.0022).
- The sound tests have timeouts. To follow a run:
  - `CASHGEAR_TEST_LOG=<file>` logs every step of the recorder: sound pipe opened, progress every 5 s, each step of Stop. The app logs the same at Debug level.
  - `FFREPORT` makes ffmpeg write a debug report. Escape the drive's colon, for example `file=C\:/Temp/ff-%t.log:level=48`.

```powershell
$env:CASHGEAR_TEST_LOG = "$env:TEMP\recorder-test.log"
dotnet test --project tests/CashGear.Capture.App.Tests -- --explicit only --filter-method "*Real_sound_played_while_recording*"
```

### Pending: install on a fresh Windows 10 22H2 and Windows 11

Needs a machine or VM (or Windows Sandbox) without the development tools:

1. Copy `CashGear.Capture-win-Setup.exe` over, run it, and accept SmartScreen's "More info › Run anyway" (the build is unsigned).
2. The app starts and says where it is. The tray icon (under ^) opens the menu.
3. Take a region capture (PrintScreen) and a full-screen capture, record 5 s, and use Capture Text. Then check Capture History and the editor.
4. Settings › General › Start with Windows, then sign out and back in: the tray icon appears.
5. Apps & Features › CashGear Capture › Uninstall with the app running. Task Manager shows no CashGear Capture or ffmpeg, `%LocalAppData%\CashGear.Capture` is gone, and the app doesn't start at the next sign-in.

### Pending: mixed DPI and negative coordinates

These need display-setting changes, so they are left for a manual run:

1. Settings → Display: set DISPLAY1 to 125% or 150%. Restart CashGear Capture.
2. Check the log: DISPLAY1 shows `dpi=120` / `dpi=144` with unchanged physical bounds.
3. Capture Full Screen: `DISPLAY1.png` must still be 1280×1024 and `_all.png` 3200×1082.
4. Drag DISPLAY1 to the left of the primary. Restart, capture again: the log shows DISPLAY1 at a negative X, and the images keep the same sizes with DISPLAY1 on the left of `_all.png`.
5. Run `dotnet test --solution CashGear.Capture.sln` in each layout. The interactive tests re-derive expectations from the live layout; `RegionOverlayWindowTests` checks overlay placement, pixel mapping and 1:1 display on every monitor.
6. Capture Region from the tray: drag a rectangle that crosses from one monitor onto the other, then click a window on the 150% monitor. Check the logged rectangle and the file size, then check that the loupe and label look the same size on both monitors.
7. Restore the original layout.

## Phase 10 results (2026-10-17)

Same PC as above: Windows 10 Pro 22H2 (19045), Xeon E5-2680 v3 (12 cores, 24 logical CPUs), GeForce GTX 1650 with driver 457.51, two monitors at 100% (`DISPLAY2` primary 1920×1080). FFmpeg is the LGPL shared build `n9.0.2-17-g2a571b6068-20260930`. Sound goes to "Headphones (H5)" and the microphone is "Microphone (H5)", a ZOOM H5 over USB.

Encoder detection here:

| Encoder | Result |
|---|---|
| `h264_nvenc`, `hevc_nvenc`, `av1_nvenc` | fail: "Cannot load cuMemAllocAsync" (the driver is older than FFmpeg's NVENC needs) |
| `h264_qsv` | fail: "Error creating a MFX session: -9" (no Intel GPU) |
| `h264_amf` | fail: "DLL amfrt64.dll failed to open" (no AMD GPU) |
| `h264_mf_hw`, `hevc_mf_hw` | **work**: NVIDIA H.264 / HEVC Encoder MFT (the GPU's encoder through Media Foundation); quality mode honoured |
| `h264_mf` | **works**: Microsoft's H264 Encoder MFT (software); quality mode honoured |
| `libopenh264` | **works** |

Automatic therefore records with `h264_mf_hw`.

Media Foundation quality calibration (testsrc2 1920×1080 at 30 fps, 4 s, SSIM against the source, compared with the old x264 settings):

| | x264 CRF 30 | x264 CRF 23 | x264 CRF 18 |
|---|---|---|---|
| x264 (old) | 0.9833, 1.1 MB | 0.9938, 2.9 MB | 0.9973, 4.6 MB |
| NVIDIA MFT, quality 40 / 55 / 65 | 0.9912 (q40) | 0.9952 (q55) | 0.9969 (q65) |
| Microsoft MFT, quality 55 / 75 / 90 | 0.9855 (q55) | 0.9949 (q75) | 0.9976 (q90) |
| OpenH264 2 / 4 / 8 Mbit/s | 0.9739 | 0.9831 | 0.9922 |

The presets (Small / Balanced / High / Near lossless) use hardware quality 35 / 50 / 65 / 90, software 55 / 72 / 88 / 100, and OpenH264 0.05 / 0.1 / 0.18 / 0.35 bits per pixel.

| Check (SyncTest unless noted) | Result |
|---|---|
| 30 s trial, 1920×1080 at 30 fps, Automatic (NVIDIA MFT), system audio: 901 frames, 0 dropped; 15 flashes, 15 beeps, all paired; offset −34.0 ms (sd 0.0), drift 0.0 ms | pass |
| 60 minutes, 1920×1080 at 30 fps, Automatic, system audio, 3 pauses of 5 s (`run --minutes 60 --pauses 3`), run 1 | pass for the 29:38 the screen was drawn: 890 claps, drift −4.8 ms (fit) / −4.9 ms (first to last 5 min), offset −44.9 ms (sd 5.2). Then the power plan turned the display off (30 min without input; `VIDEOIDLE` 1800 s) and the video froze: 54,455 frames repeated. Led to the display-on request |
| The same, run 2, with the recorder keeping the display on (`SystemExecutionState` 0x3 while recording) | pass: the display was still off at the start and came back at 22:50; from there 1,116 claps, offset −40.2 ms in every 10-minute stretch to the end, drift 0.0 ms. 108,001 frames, 0 dropped, 1,801 beeps, CPU 1.6% (0.39 cores), 187.7 MB |
| 10 minutes with the software encoder and a square repainting every frame (`--minutes 10 --busy --encoder software`) | pass: 18,001 frames, 0 dropped, 300 of 300 claps; offsets −82 or −49 ms (a frame apart), first and last minutes both −82.0 ms; CPU 4.1% (0.99 cores) |
| CPU: 5 minutes with the NVIDIA MFT and a square repainting every frame (`--minutes 5 --busy --encoder h264_mf_hw`; every picture read back) | pass: 1.9% of 24 logical CPUs (0.46 cores) for the whole process, 9,001 frames, 0 dropped, 150 of 150 claps, drift 0.0 ms. Target < 10% |
| Killed (`Stop-Process -Force`) after 12 s, 25 s and 1.5 s of recording, during a pause at 10 s of recording (`--pauses 1`), and at 60 fps after 40 s; then `recover` | pass: 11.0 s, 24.0 s, 1.0 s, 9.0 s, 39.0 s saved; each "cut short", decodes without errors, offsets −32 to −17 ms, no drift |
| H5 USB cable pulled 30 s into a 3-minute recording with system audio + the H5 microphone (`--microphone default --tracks Both`) | pass: "system audio (Headphones (H5)) stopped working … The recording continues without it." and the same for the microphone, once each; 180.03 s recorded, 5,401 frames, 0 dropped; Mixed, System audio and Microphone tracks each 180.03 s, silent (−91 dB) after the unplug |
| Recorder tests on the desktop (`ScreenRecorderTests`, 14): pause, change during a pause, microphone failure, encoder fault giving two parts, stalled encoder, sound placement, release while a frame waits | pass |
| Settings window's Recording tab and the toolbar's sound panel opened on the desktop by the tests | pass |
| `tools\package.ps1 -OutputDir artifacts\package-check` (version 1.0.1, unsigned) | pass: `ffmpeg\` holds exactly the five LGPL libraries, `LICENSE.txt` and `COPYING.GPLv3` (checked by the script); Setup.exe 130.8 MB (1.0.1: 118 MB). Not installed: installing from a session shell lands in Claude's virtualized AppData (see "Tray icon conflict after 1.0.0") |
| Explicit `Real_sound_played_while_recording_lands_where_it_was_played` (beeps through the H5) | 4 of 5 runs pass (481,600 sound frames, beeps within 30 ms across the pause). The first, right after the packaging run, found no sound stream in the file and left no log; not reproduced since |

Notes:
- **The offset is constant, not drift.** In the file the beep is 17–34 ms before the flash: the flash is drawn by WPF and composed by DWM a frame or so after it is asked for, while the beep's time comes from the sound card. At 30 fps the flash lands on whole frames, so single offsets vary by up to a frame (33 ms); the drift figures come from a line fitted through all of them.
- **SyncTest beeps go to the default output device**, and the H5 is that device here. Unplugging it ended the beeps and flashes too ("Beeps stopped: The audio device has been disconnected…"), which is why the unplug run pairs only the first 12 claps.
- **The kill runs' logs only show the first progress line.** Progress is printed every minute for runs over 5 minutes, and they were killed earlier; the time recorded is the time waited after the first line.

### Pending: hardware encoders through FFmpeg
NVENC needs a current NVIDIA driver here. Quick Sync and AMF need an Intel or an AMD GPU. After installing one, Settings › Recording › **Check again**, then:
`SyncTest run --minutes 5 --busy --encoder h264_nvenc` (or `h264_qsv`, `h264_amf`). The report gives the CPU figure.

## Tray icon conflict after 1.0.0 (2026-10-16)

Same PC. Setup.exe for 1.0.0 had been run from a shell started by Claude's desktop app, a packaged (MSIX) app. Windows redirected the install into `%LocalAppData%\Packages\Claude_…\LocalCache\Local\CashGear.Capture`, but that copy saw `%LocalAppData%\CashGear.Capture`. Commands that need the real AppData were run outside the package through `Win32_Process.Create`.

| Check | Result |
|---|---|
| Setup.exe run from Explorer: "Windows refused the tray icon" for 2 minutes, then the warning. A restart at 22:35 was refused the same way | fail (the bug) |
| Hidden copy deleted, installed 1.0.0 started again from outside the package | still refused (Windows doesn't free the id when the file is deleted) |
| Development build copied to one path twice, in Claude's LocalCache and in the real AppData, without ffmpeg. The LocalCache copy, started inside the package, got the usual id `168fe1c9…` | pass (claims the id like the hidden install did) |
| The real copy at the same path, started outside the package: usual id refused, "held by another copy … using fallback id 1", icon created on attempt 1 as `ddd3206e…` | pass (1.0.0 only tries the usual id, so it failed here) |
| Both test copies exited with `--exit` and deleted | done |
| 2026-10-17, the message after the last refusal: a temporary copy of the development build in the scratch folder (not committed) that always refuses the icon, gives up after 2 attempts, and uses its own instance names and data folders, so the installed app kept running. `--exit` sent while the message was open: "Received instance command Exit", "UI exiting" 43 ms later, "Shutdown complete" 60 ms after that, process gone 1.05 s after the `--exit` copy started. Same again with a screenshot: same text, warning icon and OK button | pass (1.0.0 stayed alive until OK was clicked) |

## Copy running as administrator (2026-10-17)

Same PC; UAC is set to elevate administrators without prompting. Release builds of 1.0.0 (`main` at 3f2407f) and of the fix ran from a scratch folder. Normal launches were started outside Claude's package through `Win32_Process.Create`, as Explorer would start them; elevated ones with `Start-Process -Verb RunAs`. Message boxes were read through their window text. The installed copy was closed for the test (its tray-icon warning box had to be dismissed first; it had received `--exit` 32 s earlier) and started again afterwards.

Cause, with a small probe (not committed) that creates and opens the pipe with 1.0.0's options:

| Check | Result |
|---|---|
| Elevated process: token owner `BA` (Administrators), integrity High. Its pipe: `O:BA D:(A;;0x1f019f;;;BA)`, no integrity label | admits Administrators only |
| Normal process: token owner is the user, integrity Medium. Its pipe: owner and only ACE the user | admits the user |
| Normal client → elevated pipe, inside and outside Claude's package: "Access to the path is denied"; `CreateFile` error 5 even for `READ_CONTROL` alone, so the DACL refuses it, not an integrity label | refused (the bug) |
| Elevated client → elevated pipe | sent |
| Elevated client → normal pipe: "Could not connect to the pipe because it was not owned by the current user" | refused (second bug) |
| Normal client → normal pipe | sent |

The app:

| Check | Result |
|---|---|
| Fixed build started as administrator; log: "(PID 12248, as administrator)" | pass |
| Normal start: "CashGear Capture is already running as administrator. Windows only lets programs that also run as administrator talk to it, so this copy can't hand over to it. …"; exit code 1, the elevated copy keeps running | pass |
| Normal `--exit`: "… so --exit can't close it from here. Exit it from its tray icon (› Exit), or run the command again as administrator."; exit code 1 | pass |
| Normal `--capture-region`: "… so --capture-region can't reach it from here. …"; exit code 1, nothing received | pass |
| 1.0.0's normal `--exit` against it: no message, exit code 1 | fail (the bug) |
| Elevated `--exit`: the elevated copy shut down | pass |
| Fixed build started normally; elevated start: `Activate` forwarded, exit code 0 | pass |
| 1.0.0's elevated `--exit`: "not owned by the current user", exit code 1, copy kept running | fail (the second bug) |
| Fixed elevated `--exit`: the normal copy shut down | pass |
| Installed copy started again | done |

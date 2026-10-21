# Testing

## Automated

```powershell
dotnet test --solution CashGear.Capture.sln
```

Tests tagged `Category=Interactive` need a logged-in, unlocked desktop. They skip themselves when none is available or when `CASHGEAR_SKIP_INTERACTIVE=1`. On Windows 10 the WGC tests flash a yellow border around each monitor — expected.

The recording and Media tests use the real FFmpeg libraries from `tools\ffmpeg` (run `tools\get-ffmpeg.ps1`), plus that build's `ffmpeg.exe` and `ffprobe.exe` to check files independently, and skip without them. Encoder detection for the recorder tests is cached in `%TEMP%\cashgear-tests\encoders`. Tests that capture the screen or show windows run one at a time, also across the test assemblies, which run side by side (a named semaphore; a Windows 10 capture border or another test's window would otherwise show up in a test's pixels), and tests that send input with `SendInput` hold another, so their clicks and keys only reach their own windows; the region-overlay pixel test can also fail while another app draws over the screen (for example a "using your computer" indicator).

The WGC leak soak test is explicit:

```powershell
dotnet test --project tests/CashGear.Capture.Engine.Tests -- --explicit only
```

So is the real-sound recording test, because it plays quiet beeps through the default output device for about 10 s:

```powershell
dotnet test --project tests/CashGear.Capture.App.Tests -- --explicit only
```

And the per-app acceptance test, which plays a 440 Hz and a 1 kHz tone (amplitude 0.2) from two helper processes for about 5 s and checks that "only one app" hears only the first and "all except one app" only the second:

```powershell
dotnet test --project tests/CashGear.Capture.Audio.Tests --filter-method "*Only_the_chosen_app_is_recorded*" --explicit only --output Detailed
```

And the browser password test, which opens Microsoft Edge on a page whose password field has the focus and types into it (it types into the window in front, so don't use the PC meanwhile):

```powershell
dotnet test --project tests/CashGear.Capture.Overlays.Tests --filter-method "*browser_password*" --explicit only
```

The webcam tests use a fake camera that stamps its pictures like a real one; one test lists and opens the real cameras (none here).

The noise-suppression tests use the real RNNoise from `tools\rnnoise` and skip without it. Build it once with `tools\get-rnnoise.ps1`, which needs Visual Studio's C++ compiler ("Desktop development with C++").

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
| Dual monitor 100% + 100% (secondary right of primary, offset Y) | Phase 0–9 and sound in recordings pass (2026-10-15); Phase 13 passes (2026-10-21) |
| Dual monitor, mixed DPI (100% + 150%) | Phase 13 passes with the primary at 150% (2026-10-21; the whole suite, one Phase 12 failure); the by-hand steps for Phases 1–2 **pending** — see "Pending" below |
| Monitor left of primary (negative X) | Phase 13 passes with the 100% monitor at (−1280, 200) and the primary at 150% (2026-10-21); the by-hand steps for Phases 1–2 **pending** — see "Pending" below |
| Windows 10 22H2 | Phase 0–9 and sound in recordings pass on the development PC (2026-10-15); Phase 10 passes (2026-10-17); Phase 11 passes what was run (2026-10-17; manual sound checks **pending**, below); Phase 12 passes what was run (2026-10-19, the Edge password field included; a real webcam **pending**, below); Phase 13 passes what was run (2026-10-21; by-hand checks **pending**, below); fresh install **pending** |
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

Step 5 ran in Phase 13 (2026-10-21) with the primary at 150%, the other monitor at 100% right of it and then left of it at negative X: see "Phase 13 results". It found a Phase 12 failure at 150% (Phase 13 › Pending). Steps 1–4 and 6, by hand in the app, are still pending.

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

## Phase 11 results (2026-10-17)

Same PC as Phase 10 (Windows 10 Pro 22H2 19045). The output devices are "Headphones (H5)" (the default) and "Speakers (High Definition Audio Device)"; the microphone is "Microphone (H5)". There is no C++ compiler here, so `rnnoise.dll` isn't built and the 6 RNNoise tests skip.

| Check | Result |
|---|---|
| Process loopback on Windows 10 19045 (a probe before the work, then `ProcessLoopbackTests`) | pass: include and exclude both activate; 2 s of this test process's (silent) sound arrives with Windows' timestamps on every packet and no frame lost between the 10 ms polls |
| Explicit `Only_the_chosen_app_is_recorded_while_another_plays_too`: 440 Hz and 1 kHz from two PowerShell processes | pass: only the 440 Hz process → 440 Hz 0.1957, 1 kHz 0.000058 (71 dB down); all but it → 1 kHz 0.1957, 440 Hz 0.000051 (72 dB down). The tones were played at 0.2: process loopback is before the device volume |
| The real default microphone (H5) recorded for the microphone test, 0.5 s, while the meters listened | pass: 24,000 frames, with sound on the meter |
| Loudness through the remux (`LoudnessTests`, measured by `ffmpeg.exe -af ebur128`) | pass: −30.6 LUFS → −15.9; −5.1 LUFS → −16.0; a quiet track with loud beeps → −16.0, true peak −5.7 dBFS; the sound within 2 samples of its place, video frames identical, a silent track copied |
| A recording made with "Even out loudness" (a generated 1 kHz tone at 0.05), and a recovered one | pass: both −16 ± 1 LUFS |
| The microphone limited at −1 dBFS with the compressor turned on mid-recording (a generated tone at 1.8× full scale) | pass: peaks 0.85–0.92 after the onset, 0.4–0.6 with the compressor, the tone at 1.00 s as made |
| Settings window: Recording page shows two meters, which listen only while it is shown | pass (desktop test) |
| `tools\package.ps1 -Version 1.0.1-phase11 -NoRnnoise` into the scratch folder | pass: `ffmpeg\` holds exactly the six libraries (now with `avfilter-12.dll`), `LICENSE.txt` and `COPYING.GPLv3`; Setup.exe 143.7 MB (Phase 10: 130.8 MB) |
| `tools\get-rnnoise.ps1` without the C++ workload | as designed: the tarball's SHA-256 checks out, it extracts, and it stops with how to add the workload |
| The whole suite, after each milestone and at the end | 738 tests: 729 pass, 9 skip (6 RNNoise, 3 explicit). Two desktop tests failed once each while the screen was busy and passed alone: `WindowCaptureTests.Wgc_captures_the_visible_frame…` and `WgcCaptureTests.Matches_gdi_for_every_monitor` (96.998% against 97%) |

### Pending: devices during a recording
With a test recording of system audio and the H5 microphone running (track layout Both):
1. In Windows' Sound settings, make "Speakers (High Definition Audio Device)" the default output, then the H5 again. Expect a notification "System audio now comes from Speakers…" each time, the sound following, and the saved notification listing both switches with their times.
2. Unplug the H5's USB cable for about 10 s, then plug it back in. Expect "…was disconnected" (microphone; system audio switches to the speakers if Windows makes them the default), then "…works again", silence in between, and no dropped frames.
3. Choose the H5 by name (not the default) under Settings › Recording › System audio from and repeat step 2: it should be waited for and recorded again.

### Pending: RNNoise
1. Add "Desktop development with C++" to Visual Studio 2026 in the Visual Studio Installer, then run `tools\get-rnnoise.ps1` and rebuild. The RNNoise tests then run: the delay (expected 960 frames), more than 20 dB off noise alone, strength, switching on mid-way, the chain's timestamps.
2. Acceptance: Settings › Recording, turn on noise suppression (100%), and with a fan or typing nearby, **Record 5 seconds** while talking, then play it as recorded and processed. For a file: record 10 s with the processing off and on (a track per source), and compare the microphone tracks' noise floor (`ffmpeg -i file.mp4 -map 0:a:2 -af astats -f null -`) and spectrograms (`-lavfi showspectrumpic=s=1024x512`).
3. CPU: watch the app's CPU in Task Manager during a few minutes of recording with the microphone, noise suppression on and then off.

### Pending: push-to-talk and a real recording's loudness
- Set Settings › Hotkeys › Push to talk to F13 (or Scroll Lock), record 30 s talking with and without the key held: the microphone track is silent except while it is held and a quarter of a second after; the toolbar says "Talking".
- Record a minute of speech with "Even out loudness" on, and measure it with `ffmpeg -i file.mp4 -map 0:a:0 -af ebur128 -f null -` (expect −16 ± 1 LUFS).

## Phase 12 results (2026-10-19)

Same PC as Phase 11 (Windows 10 Pro 22H2 19045, GTX 1650 on driver 457.51). There is no camera, so the webcam is tested with a fake camera that stamps its pictures like a real one. The keyboard layout here is sometimes Arabic. The desktop tests drive the mouse and keyboard with `SendInput`, always into their own test windows. Tests that send input hold a named semaphore (`Local\CashGear.Tests.SyntheticInput`), because the test assemblies run side by side: one test's click would land in another's window.

| Check | Result |
|---|---|
| Live overlays, acceptance (`OverlayRecordingTests`): a 320×240 area at 30 fps over a test window that turns blue on a click and green on a key; SendInput clicks it at about 0.8 s and presses Ctrl+S at about 1.6 s | pass, 4 of 4 runs: the ring and the caption first show on exactly the frame of the click's and the key's hook time (frames 25 and 49), and the window's own reaction shows 0–1 frames later |
| Overlays added later, acceptance: the same recording made clean with the metadata, then **Add overlays** | pass: ring on frame 25 and caption on frame 49, as live; every picture kept (79 of 79); the sound copied |
| A password field, acceptance (`Keys_typed_into_a_password_box_go_nowhere`): click a WPF PasswordBox, type two keys, click a TextBox, type one | pass: 2 keys hidden, 1 shown; neither the timeline nor the journal has the hidden ones; the PasswordBox did get them |
| A browser password field, acceptance (explicit `Typing_in_a_browser_password_field_shows_no_keys`, run with the user's go-ahead while the PC was free): Microsoft Edge on a page whose `<input type=password>` has the focus; "secret" and Ctrl+A typed into it with "All keys" on | pass, 3 of 3 runs: 0 keys shown, 7 hidden, no caption; the privacy check found a "password field" in `msedge.exe` (Chromium answers UI Automation). The first run's cleanup failed: the process the test started hands over to another Edge process and exits, so the test now closes the browser found by its window, with all its helpers |
| UI Automation through the source-generated COM interfaces | pass: the focused PasswordBox reads ClassName "PasswordBox", this process's ID and IsPassword true; a TextBox reads false; an excluded program is private |
| Privacy timing with a scripted check: a key waits for the check after a focus change, is hidden past 750 ms, a change 0–40 ms after a key counts for it, no UI Automation hides keys | pass |
| Hooks: SendInput move, click and F24 into a test window | pass: in order, with QPC times, marked injected; no keyboard hook without keystrokes; one set of hooks at a time |
| Direct2D compositor on the GTX 1650: ring, translucent dot, spotlight (half as bright 200 → 100 far away), halo, pointer at 2× from its hotspot, caption box with white text, webcam circle with border and mirror, rounded rectangle kept inside the frame | pass (pixel checks) |
| Click sound | pass: the tick at 0.8246 s for a click at 0.8245 s; its own track when it is the only sound |
| The pointer drawn by the app at 2× | pass: 225 white and 28 black pixels where the arrow is (its thin edge is softened by H.264) |
| Webcam, acceptance: a fake camera flashes when a generated microphone tone beeps (both stamped from one origin) | pass: beeps at 1.362/2.362 s; the webcam's own track and the webcam drawn over the screen flash at 1.367/2.367 s (the first frame after the beep). The saved MP4 has a second video track "Webcam" (not the default) |
| The webcam over 30 minutes: 30 simulated minutes of a 29.97 fps camera with ±8 ms jitter, read at 30 fps (`TimedQueueTests`) | pass: no frame ever shows a picture more than one camera frame and the jitter old (49.4 ms), in the last minute as in the first |
| Webcam merge (`RemuxExtraTests`): both flashes at 1.500 s, a part's window, a missing file; a recovered recording keeps its webcam track | pass |
| A camera that sends nothing; no camera | pass: the recording has no webcam, a warning, and the note says why |
| Real camera list and opening on this PC (no camera) | pass: an empty list, and "No webcam is connected…" |
| Drawing (`DrawingLayerTests`): the pen on, a 200 px drag across the area with SendInput | pass: one stroke, in the recording (180 red pixels along it), the click that drew it not shown as a ring, drawing on/off in the metadata |
| The drawing hotkey | pass (fake hotkey service): registered only while recording; a key another action has is reported before recording |
| Metadata: a recording's file has a row per frame written, the click at its time (±0.1 s) and position; a recovered recording gets its file | pass |
| Settings › Overlays (window and view model), the webcam preview with the fake camera (and the camera let go when the webcam is turned off), the toolbar's keystroke switch | pass |
| The app's whole service container builds (new `AppHostTests`) | pass |
| The whole suite | 813 tests: 802 pass, 11 skip (6 RNNoise until it is built, 4 explicit, the real-microphone test: no microphone right now). With the desktop lock, the two WGC tests that used to fail in full runs (`Wgc_captures_the_visible_frame…`, `Matches_gdi_for_every_monitor`) passed; before it, the first failed in most full runs, because another assembly's top-most test window covered its red cover |

### Pending: a real webcam
No camera is connected to this PC. With one:
1. Settings › Overlays › Webcam: turn it on, pick the camera, and check the preview shows it (the camera light comes on) and that dragging places it.
2. Record a minute with the microphone and the webcam, clapping in front of the camera now and then. In the saved MP4, the clap's sound and the hands meeting should be within a frame, in the webcam drawn over the screen and in the "Webcam" track (`ffprobe` lists it as the second video stream; VLC › Video › Video Track shows it).
3. Acceptance: the same over 30 minutes, clapping at the start and the end. Both claps should be within a frame of their sound.
4. Open the camera in another app first (e.g. Camera): the recording should still get it (shared), at that app's format.

### Pending: a real login
The explicit Edge test passes (above). By hand, on a real site: record with keystrokes on "All keys", log in somewhere in Edge or Chrome, and check the video and its `.cgmeta.json` show the user name but nothing of the password.

### Pending: by hand
- **Drawing.** Press `Ctrl+Shift+D` while recording, draw with each tool, Ctrl+Z, Delete, Esc; set "Drawings fade away after" to 3 s and check they fade.
- **Click sound.** Listen to a recording with click sounds on.
- **Pointer.** Check the 2× pointer and hiding it when idle look right on a second monitor at another scale (the mixed-DPI matrix is still pending).

## Phase 13 results (2026-10-21)

Same PC as Phase 12 (Windows 10 Pro 22H2 19045, GTX 1650 on driver 457.51), two monitors: `DISPLAY2` primary 1920×1080 at (0, 0) and `DISPLAY1` 1280×1024 to its right, offset in Y. The desktop tests drive the real overlay, toolbar and tools with `SendInput` over their own test windows (a top-most window showing a fixed, position-dependent pattern, or a window of buttons), on every monitor in turn, holding the input semaphore; results are checked against GDI grabs of the screen.

| Check | Result at 100% + 100% |
|---|---|
| Region, fixed-size, freehand, polygon and rounded captures end to end (`CaptureModesTests`): the real coordinator, overlay and screen capture, driven by SendInput | pass on both monitors: each result has exactly the expected rectangle, every fully covered pixel equals the screen, everything outside the shape is transparent white, the kept size and radius are saved |
| **Repeat Last Capture, acceptance**: each of the five modes repeated with no overlay | pass on both monitors: the identical pixel rectangle, identical pixels and transparency, after the same kind's delay |
| Repeat of a window capture | pass: the same window while it is open; after it closes, "isn't open any more" and nothing captured; before any capture, "Nothing has been captured yet" |
| Object capture (`ObjectAndMenuCaptureTests`): the pointer on a WPF button, a click | pass on both monitors: exactly the rectangle UI Automation reports for the button; the wheel goes out to the window's visible frame; the repeat gives the same pixels |
| UI Automation through the generated COM interfaces | pass: the button's rectangle (within 1 px of where WPF put it), name "Second" and type; the chain out to the window grows; nothing but the window off it; another process's control (the taskbar's Start button) read across processes |
| Open-menu capture: a real Win32 menu (`TrackPopupMenu`) and a WPF context menu | pass on both monitors: exactly the menu window's rectangle, equal to the screen; 3 s delay asked by default; with no menu open, the message and nothing captured |
| Capture toolbar (`CaptureToolbarWindowTests`) | pass: top centre of the primary at first, left out of captures (affinity 0x11), no-activate and tool-window styles, a click on it leaves the window in front alone; dragged by its grip to 15 px from each monitor's left edge it docks flush as a column, is remembered and comes back exactly there (3 of 3 runs) |
| GDI leaves out a window excluded from capture | pass (what the magnifier relies on) |
| Magnifier | pass on both monitors: the lens shows exactly the screen around the pointer (compared with a grab after it closed), centred on it; the wheel zooms 3× → 4× through the mouse hook; a click closes it and reaches no window; the zoom is kept |
| Colour picker: a SendInput pick of a pattern pixel | pass on both monitors: the exact pixel, copied as HEX, first in the history, C# format shown |
| Screen ruler | pass on both monitors: 600 screen pixels long and 56 DIPs thick at the monitor's scale, the monitor's DPI for inches, the pointer readout 123 px at 123 px, turned and remembered, left out of captures |
| Crosshair | pass on both monitors: the 1-px lines through the pointer across the virtual screen, the readout with the monitor and its own coordinates, left out of captures, a click at the crossing reaches the button under it, and a capture across the lines equals the screen without them |
| Core: shape masks, every selection model, delays, hotkeys, state.json, docking, menu rules, colour formats, ruler marks, lens maths, switches | pass |
| The whole suite | 941 tests: 930 pass, 11 skip (the same 11 as Phase 12: 6 RNNoise until it is built, 4 explicit, the real-microphone test with no microphone then) |

### Mixed DPI and negative coordinates

The user changed the displays by hand. Each layout ran the whole suite, then the Phase 13 desktop tests again after any fix:

| Layout | Phase 13 tests | The whole suite (941 tests) |
|---|---|---|
| `DISPLAY2` primary at 150% (1920×1080, 144 dpi), `DISPLAY1` at 100% right of it at (1920, 58) | pass, after three fixes: the toolbar wraps when a column is taller than the work area, is moved onto its monitor before it is measured, and the crosshair's lines are put back after a DPI change (CHANGELOG › Fixed during development) | 930 pass, 10 skip (the microphone test ran), 1 fail: `CursorShapeTests` (Phase 12, below) |
| The same, with `DISPLAY1` left of the primary at (−1280, 200) | pass; the crosshair test failed once because it placed its own button window in DIPs, off the screen; placed in physical pixels, it passes | 929 pass, 10 skip, 2 fail: `CursorShapeTests` and that crosshair test (since fixed) |
| Back to 100% + 100%, `DISPLAY1` at (1920, 145) | the 20 desktop tests pass | (the run above) |

### Done: the pointer's shapes at 150% (2026-10-21)
With the primary monitor at 150%, `CursorShapeTests.The_arrow_and_the_text_beam_become_pixels` failed. Only the I-beam was at fault: Windows scales it to a 48 × 48 colour pointer without alpha whose mask is all set and whose beam is white (it inverts the screen), and that read as fully transparent. Fixed in `CursorShapes` (CHANGELOG › Phase 13 › Fixed during development); `An_inverting_pointer_in_colour_is_drawn_like_one_in_black_and_white` covers it without changing the display.

| Layout | `CursorShapeTests` (3) | Overlays tests (20) |
|---|---|---|
| `DISPLAY2` primary at 150% (144 dpi), set by hand | pass (the I-beam: 94 black pixels, edged in white) | 19 pass, 1 skip (the explicit browser test) |
| Back to 100% + 100% | pass | 19 pass, 1 skip |

### Pending: by hand
- **Each capture mode** from the tray, the hotkeys and the toolbar in the installed app: fixed size (Tab through the sizes, arrows, a drag), freehand, polygon (Backspace, a click on the first point), rounded (the wheel), object (the wheel out to the window) and menu (a real app's menu within the 3 s). Open each PNG in another app to see its transparency.
- **Object and menu capture in other apps:** Edge or Chrome, Office, Explorer, a UWP app (its in-window flyouts can't be told apart: Known limitations).
- **Repeat Last Capture** with `Ctrl+Alt+Shift+PrintScreen` after each kind, and after restarting the app (it is remembered in `state.json`).
- **The toolbar:** drag it to each edge of each monitor, hide it, restart with "Show the toolbar when the app starts".
- **By eye:** the magnifier, the crosshair and the ruler are left out of every capture, so no test can see how they look: check them on both monitors at both scales.
- **Windows 11:** the toolbar and tools' exclusion from capture, and object capture through UI Automation.

## Phase 14 results (2026-10-21)

Same PC as Phase 13, at 100% + 100%. The destinations are tested against servers that the Outputs tests start themselves on 127.0.0.1: an FTP/FTPS server written for the tests, an SSH server on FxSsh with a small SFTP v3 handler, and Kestrel for HTTP. Nothing is printed, no Office app is opened and nothing leaves the PC. Word and PowerPoint files are checked with the Open XML SDK's validator (Office 2019 rules).

| Check | Result |
|---|---|
| Settings: schema 4 → 5 (`SettingsMigrationTests`) | pass: all 8 combinations of the old switches become File, Clipboard, Editor in that order, each on or off as it was. A workflow already in the file wins, and a file without the switches gets the new default |
| The workflow in the app (`OutputWorkflowTests`): the real registry and runner, files saved with WIC | pass: the three old switches still save (under the template's name), copy and open the editor with the saved file, and say nothing. Save alone says where, and a click shows the file. Copy alone uses no `{counter}` number and makes no file. A full-screen capture saves each monitor too. The steps after the editor go to it. An upload gets the saved file, or else a temporary JPEG (the chosen format) that is deleted afterwards, and Copy the link copies its link. A failed step is reported with what worked. A recording goes through its own workflow (the clipboard as a file to copy, an upload), skipping the printer. The editor sends its own file, or saves a new one |
| FTP and FTPS (`FtpDestinationTests`) | pass: an upload in passive mode into a new folder, with its link. A taken name gets " (2)" and the old file stays. Active mode works. A self-signed certificate is refused with its fingerprint before the password is sent; once trusted (pasted in lower case with spaces), Test writes and deletes its file and the upload is encrypted (AUTH TLS, PROT P). A changed certificate is refused. Implicit TLS works. A wrong password, an anonymous login to the login folder and a server that isn't there are all handled |
| SFTP (`SftpDestinationTests`) | pass: an unknown host key is refused with its fingerprint before logging in; once trusted, Test, an upload and " (2)" work. Logins with an RSA key file (PKCS#1), and with an encrypted PKCS#8 key without and then with its passphrase. A server with another key is refused before the password is sent. A wrong password is reported, and a missing user, password or key is reported without connecting |
| HTTP upload (`HttpUploadDestinationTests`, `LinkPathTests`) | pass: the file (its name and type), the form fields, the headers and the secret header reach the server, and the link is read from `data.link`. Also: a plain-text reply; a 401 with the start of the reply; replies without the link, with something that isn't a link, or not JSON; a secret header with no value saved, which isn't sent; addresses that aren't http(s); Test's 1 × 1 PNG; a recording as `video/mp4`; a server that isn't there. The dot path: names, `[n]`, `$.`, a match without regard to case, and errors that say where it went wrong |
| Word and PowerPoint (`OfficeDestinationTests`) | pass: a new document holds the exact picture, shrunk to the text width in proportion; a small capture keeps its size on the screen (192 px at 200% is 1 inch). A chosen landscape document takes two captures, keeping its content and page settings, with unique ids. A new presentation has one 16:9 slide with the picture centred; two more slides are added with unique ids from 256. A document open elsewhere is refused and left unchanged. A text file, a missing file and no file chosen are reported. Taken names get " (2)", and recordings are skipped. Every file passes the validator |
| Secrets (`CredentialStoreTests`) | pass: a secret reads back after a restart, and the file holds neither its text nor its plain base64. A value copied to another key can't be read. A damaged file, and values that aren't DPAPI, read as missing. Empty removes a secret. A deleted destination's secrets go and the others stay. Typed secrets wait for Commit |
| Open with (`OpenWithDestinationTests`) | pass: the command line gets the file and the folder. Programs are found by path, on the PATH and with environment variables. A capture that wasn't saved is copied and kept for the app; a saved one is opened where it is. A missing program is reported. Test starts nothing |
| The printed page (`PrintPageTests`) | pass: rendered to pixels, a fitted capture is centred inside blank margins and a filled one is cut off at them. A page is written to an XPS document and read back |
| The workflow runner (`WorkflowRunnerTests`) | pass: the steps run in order, those switched off skipped. A failing, a missing and a throwing step don't stop the rest. Copy the link copies the links made before it, or is skipped quietly. The steps after the editor are handed to it, and run at once if it can't open. A recording skips the steps for pictures only. Cancelling stops the run. Also: the registry's order, the destinations made from the settings, an item making its file once and deleting only its own, and old outgoing files cleaned |
| Settings (`SettingsDestinationsTests`) | pass: a typed password is stored only on Apply (an emptied box changes nothing), and Cancel stores nothing. A deleted server takes its secrets, and its step is marked missing. Test uses what is typed, and Trust it tests again. A new upload can be added to both workflows at once (a recording's only offers what takes videos). Steps move up and down and go. Incomplete destinations are listed as problems. The printer and Office settings are saved. An app's Test, and Word's Test of a missing file |
| The Settings window on the desktop (`SettingsDestinationsTests`) | pass: the Output tab shows the steps as check boxes. The Destinations tab shows every field and list, with the server's, upload's and app's values and three empty password boxes. A typed password reaches the window's secrets, not the store |
| The editor (`EditorSendTests`) | pass: Continue runs the waiting steps once, on the edited image. A File step makes its file the editor's. Send to lists the destinations and sends the saved file when nothing has changed. Without destinations nothing is offered |
| The whole suite | 1117 tests: 1107 pass and 10 skip (the 6 RNNoise tests until it is built, and 4 explicit), 0 fail; the real-microphone test ran. The first desktop run stopped in the App tests: the toolbar's after-capture button crashed a toolbar test (fixed). After the fix, the App tests pass (238, 1 explicit skip) |

### Pending: by hand
- **A real printer.** Each fit and orientation, and the margins. Also the print dialog when no window of the app is in front, since it may open behind other windows.
- **Office.**
  - **Word and PowerPoint:** open the new files and the ones captures were added to, and check the picture's size.
  - **LibreOffice:** the same files open there too.
- **Real servers.**
  - **FTPS:** FileZilla Server, and vsftpd with `require_ssl_reuse=YES`.
  - **SFTP:** OpenSSH with a password, an RSA or Ed25519 key, and an encrypted key.
  - **Behind a router:** passive mode through NAT, and active mode refused by a firewall.
- **A real image host or a self-hosted upload API:** the link read from its reply and copied.
- **Open with** Paint.NET or Photoshop, for a capture that wasn't saved.
- **In the installed app:**
  - the toolbar's after-capture menu;
  - Continue in the editor, and Send to;
  - a recording copied to the clipboard and pasted into Explorer and a chat app;
  - a notification's click (the file, the link).
- **Upgrading** an installed 1.0.x whose settings.json still has the after-capture switches (schema 4).

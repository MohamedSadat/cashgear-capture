# Testing

## Automated

```powershell
dotnet test --solution CashGear.Capture.sln
```

Tests tagged `Category=Interactive` need a logged-in, unlocked desktop. They skip themselves when none is available or when `CASHGEAR_SKIP_INTERACTIVE=1`. On Windows 10 the WGC tests flash a yellow border around each monitor — expected.

The WGC leak soak test is explicit:

```powershell
dotnet test --project tests/CashGear.Capture.Engine.Tests -- --explicit only
```

## Manual test matrix

| Configuration | Status |
|---|---|
| Single monitor 100% | not run |
| Single monitor 150% | not run |
| Dual monitor 100% + 100% (secondary right of primary, offset Y) | Phase 0–6 pass (2026-10-15) |
| Dual monitor, mixed DPI (100% + 150%) | **pending** — see "Pending" below (covers Phases 1–2) |
| Monitor left of primary (negative X) | **pending** — see "Pending" below (covers Phases 1–2) |
| Windows 10 22H2 | Phase 0–6 pass (2026-10-15) |
| Windows 11 23H2+ | not run |

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

### Pending: mixed DPI and negative coordinates

These need display-setting changes, so they are left for a manual run:

1. Settings → Display: set DISPLAY1 to 125% or 150%. Restart CashGear Capture.
2. Check the log: DISPLAY1 shows `dpi=120` / `dpi=144` with unchanged physical bounds.
3. Capture Full Screen: `DISPLAY1.png` must still be 1280×1024 and `_all.png` 3200×1082.
4. Drag DISPLAY1 to the left of the primary. Restart, capture again: the log shows DISPLAY1 at a negative X, and the images keep the same sizes with DISPLAY1 on the left of `_all.png`.
5. Run `dotnet test --solution CashGear.Capture.sln` in each layout. The interactive tests re-derive expectations from the live layout; `RegionOverlayWindowTests` checks overlay placement, pixel mapping and 1:1 display on every monitor.
6. Capture Region from the tray: drag a rectangle that crosses from one monitor onto the other, then click a window on the 150% monitor. Check the logged rectangle and the file size, then check that the loupe and label look the same size on both monitors.
7. Restore the original layout.

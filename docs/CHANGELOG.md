# Changelog

Each phase lists the features added, how it was tested, and known limitations.

## Phase 0 — Scaffold (2026-10-14)

### Added
- Solution `CashGear.Capture.sln`: Core (`net10.0`, no WPF/WinRT), Interop, Engine, Editor and Recording placeholders, WPF App, three test projects (xUnit v3 on Microsoft.Testing.Platform + AwesomeAssertions). Central package management; one Windows TFM property (`net10.0-windows10.0.22621.0`, minimum 10.0.19041).
- `app.manifest` with PerMonitorV2 DPI awareness; startup self-check logs the thread's DPI context.
- Generic host + DI + Serilog (`%LocalAppData%\CashGear\Capture\logs\capture-yyyyMMdd.log`, 14 days, shared between processes).
- `JsonSettingsService` (`%AppData%\CashGear\Capture\settings.json`): defaults on first run, lenient parsing, clamping, atomic writes, corrupt files backed up as `settings.json.corrupt-<timestamp>`.
- Tray icon (H.NotifyIcon, efficiency mode off) with Capture Region / Capture Window / Capture Full Screen / Settings… / Exit. Settings… opens the JSON file in Notepad until the settings window lands (Phase 4).
- Single instance: per-user/session named mutex; later launches forward a command over a current-user-only named pipe (`activate`, `--exit`, `--capture-fullscreen`) and exit.
- Core building blocks used by later phases: physical-pixel geometry, DPI math, pooled `CapturedImage`, row copy/compositing helpers, hotkey types.

### Tested
- 80 Core unit tests (geometry incl. negative coordinates, DPI rounding, row-pitch copies, compositing, image pooling, settings round-trip/corruption/concurrency, command parsing).
- Manual: launch → one process, tray icon created, log written; second launch exits 0 and the first instance logs `Activate`; `--exit` shuts the instance down in < 1 s with "Shutdown complete" logged; defaults written to settings.json.

### Known limitations
- Capture Region / Capture Window show a "coming in Phase 2/3" notification.
- Tray icon is generated from text ("CG"); no designed icon yet.

## Phase 1 — Enumeration + full-screen capture (2026-10-14)

### Added
- **Monitor enumeration** (`Win32MonitorService`): bounds, work area, effective DPI, primary first; re-enumerated on every call.
- **Window enumeration** (`Win32WindowService`): visible, non-cloaked, non-tool top-level windows in z-order with DWM extended-frame bounds; own process excluded; `WindowFromPoint` returns the root window.
- **Capture backends**:
  - GDI: `BitBlt` + `CAPTUREBLT` into a top-down DIB, alpha forced opaque, optional cursor.
  - Windows.Graphics.Capture: single-frame grab through a shared D3D11 device (WARP fallback, recreated after device loss), staging-texture copy that honours row pitch, sub-area support, border switched off where the OS allows (Windows 11).
  - Any WGC failure or timeout falls back to GDI (cancellation does not).
- **Backend policy** (`capture.backend` = `Auto` | `Wgc` | `Gdi`): `Auto` uses WGC for monitor/region captures only where the yellow border can be disabled, so GDI on Windows 10; window captures (Phase 3) will prefer WGC.
- **`ScreenCaptureService`**: monitor, all-monitor and region capture. Multi-monitor images are composed in virtual-screen space; areas outside every monitor are fully transparent (black in JPG/BMP/clipboard DIB).
- **Output**: PNG (alpha kept only when there are gaps) / JPG (quality) / BMP via WIC; unique file names, no partial files on failure. Clipboard gets Bitmap + packed DIB + PNG, set on the UI thread with retry while another app holds the clipboard.
- **Capture Full Screen** (tray and `--capture-fullscreen`): waits `hideDelayMs` (200 ms) for the tray menu to fade, captures once, writes `Capture_<timestamp>_all` plus one file per monitor (cropped from the same capture) per `capture.fullScreenOutput`, copies the stitched image to the clipboard, shows a notification.
- Startup diagnostics log every monitor, cross-check the virtual screen against `SM_*VIRTUALSCREEN`, and log the effective backend (off the UI thread).

### Tested
- 117 automated tests (80 Core, 36 Engine incl. real-desktop GDI and WGC captures, 1 Editor). Engine highlights: compositing with negative origins and gaps, fallback/cancellation, buffer return on failure, WGC vs GDI pixel agreement on both monitors and on an offset sub-area, 50-grab WGC soak with no handle/memory growth (explicit test).
- Manual on the dev machine (see TESTING.md): tray menu shows all items; Capture Full Screen from the tray and from `--capture-fullscreen` writes `_all` 3200×1082 with transparent gaps, DISPLAY2 1920×1080, DISPLAY1 1280×1024; per-monitor files match the stitched image; the tray menu is not in the capture; clipboard paste into Paint gives 3200×1082; forced `Wgc` captures via WGC; a 1 ms WGC timeout falls back to GDI; tray Exit leaves no ghost icon.

### Known limitations
- Window capture (`CaptureWindowAsync`) throws `NotSupportedException` until Phase 3; region capture has no UI until Phase 2.
- Windows 10: forcing `Wgc` flashes a yellow border around each monitor. `Auto` avoids it by using GDI, which can miss some exclusive-fullscreen / protected content and is not HDR aware.
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` fails on the WPF tray menu popup on Windows 10 (Win32 error 8); the hide delay is what keeps the menu out of captures. The Phase 2 overlay must not rely on display affinity alone.
- Saved images are tagged 96 DPI on purpose (1 image pixel = 1 screen pixel in other apps).
- File names follow a fixed pattern until the template engine (Phase 4); settings changes need a restart until the settings window (Phase 4).
- Mixed-DPI and negative-coordinate layouts are covered by unit tests but not yet checked on real hardware (steps in TESTING.md).
- Default output folder is `Pictures\CashGear Capture`, which is OneDrive-synced on machines with folder backup enabled.

## Phase 2 — Region selection overlay (2026-10-14)

### Added
- **Capture Region** (tray and `--capture-region`): waits the hide delay, snapshots the window list, freezes all monitors with one capture, shows the overlay, and crops the selection from the frozen image (pixel-exact, no second capture). Saved as `Capture_<timestamp>_region` or `_window`, copied to the clipboard like full-screen captures.
- **`RegionSelectionModel`** (Core, UI-free, physical virtual-screen pixels):
  - hover highlights the topmost window under the cursor, or the monitor when there is none; click picks it;
  - drag selects a rectangle including both corner pixels;
  - while selected: arrows nudge 1 px, Shift+arrows resize, Ctrl ×10; Enter or double-click inside confirms; Enter while hovering captures the highlighted target; Esc or right-click cancels;
  - everything is clamped to the virtual screen, so negative coordinates work.
- **Overlay window**: one borderless, opaque, topmost window spanning the virtual screen, showing the frozen image with a dim mask, the lit selection and a frame drawn outside it, crosshair guides, a W×H label (plus window title while hovering), and a nearest-neighbour loupe with coordinates and `#RRGGBB`. Before a selection exists, arrow keys move the mouse by 1 px. Chrome is sized for the DPI of the monitor it is drawn on.
  - Content lives in a physical-pixel canvas (`LayoutTransform` = 1/DPI), so input and drawing are in device pixels at any DPI mix. All DIP↔pixel conversions go through `DpiHelper`.
  - The HWND is placed with `SetWindowPos` on the exact virtual-screen rectangle and re-placed after `WM_DPICHANGED`; placement is verified after the first render.
  - Excluded from capture via `WDA_EXCLUDEFROMCAPTURE` (works because the window is not layered).
  - Closes and cancels if the display layout changes.
- Later launches call `AllowSetForegroundWindow` before forwarding a command, so the running instance can activate the overlay.
- Window enumeration skips click-through (`WS_EX_TRANSPARENT`) windows, which are never meaningful targets.

### Deviations from the spec
- **One spanning window instead of one per monitor.** A non-active topmost window stays under its monitor's taskbar. With per-monitor windows only the active one ever gets above its taskbar. One activated full-screen window gets above every taskbar. The physical-pixel canvas removes the DPI issue that made the spec prefer per-monitor windows.
- **No `AllowsTransparency`.** The frozen screenshot is the background, so the window can be opaque. That is faster than a layered window and lets display affinity work.
- **A drag doesn't capture on release.** The selection stays adjustable until Enter or a double-click, as the spec's nudge/resize/Enter list implies.

### Tested
- 154 automated tests (153 pass, 1 explicit). New: 25 `RegionSelectionModel` tests (negative coordinates, spanning windows, click vs drag, inclusive corners, clamping, nudge/resize, confirm/cancel). New App.Tests:
  - `DpiHelper` unit tests;
  - an interactive end-to-end overlay test. It covers the exact virtual-screen rectangle, maps every probed screen pixel on every monitor to itself, and shows the frozen image 1:1: at least 99% exact, and better than either one-pixel shift.
- Manual on the dev machine (overlay opened from the tray, so activated):
  - a click on the taskbar strip reached the overlay, so the overlay covers the taskbar;
  - Esc cancels with no file;
  - clicking maximized Notepad captured exactly its frame (0,0 1920×1040);
  - a drag from (13,40) to (527,132), then Right ×2 and Shift+Down, captured (15,40 515×94). Its text pixels line up 100% with the same rectangle of the window capture, and only 21–31% at any one-pixel shift.

### Known limitations
- Mixed DPI (100% + 150%) and a monitor left of the primary are covered by the model tests and the overlay test's live-layout checks, but not yet run on such a layout (see TESTING.md).
- If the app can't take the foreground (e.g. `--capture-region` from a background process), the overlay opens inactive. The taskbar then stays on top and keyboard input waits for the first click.
- Moving a selection by dragging inside it, and resize handles, are not implemented (keyboard only).
- Picking a window crops its rectangle from the frozen screen, including anything that overlaps it. True window capture (occluded windows, shadow) is Phase 3.
- Very large virtual screens (wider or taller than 16384 px) may exceed WPF's texture limit.

## Phase 3 — Window capture + global hotkeys (2026-10-14)

### Added
- **Window capture** (`CaptureWindowAsync`).
  - **Backend.** WGC uses `CreateForWindow`, so a window's own content is captured even where other windows cover it. The frame is cropped to the visible window frame (invisible resize borders dropped), and alpha is kept for shaped corners.
  - **Policy under `Auto`.** A covered or partly off-screen window always uses WGC, as does every window on Windows 11. A fully visible window on Windows 10 is taken from the screen with GDI, so no yellow border flashes. If WGC fails, the screen pixels are used instead.
  - **Errors.** Minimized and vanished windows get a clear error instead of a capture.
  - **Options.** Include the cursor; include a synthesized, DPI-scaled drop shadow on a transparent margin (`capture.includeWindowShadow`).
- **Capture Window** (tray, `--capture-window`) opens the overlay in window-only mode. One click on a window picks it, then the window's own content is captured.
- **Global hotkeys** (`RegisterHotKey` + `MOD_NOREPEAT` on a message-only window):
  - Defaults per the spec: PrintScreen → region, Alt+PrintScreen → the window that was in the foreground at the press, Ctrl+PrintScreen → full screen, Ctrl+Shift+PrintScreen → scrolling capture (a "coming in Phase 6" notice for now).
  - Hotkeys are written as text in `settings.json` (`hotkeys` section; `Ctrl+Shift+PrintScreen`, aliases such as `PrtSc`, `null`/empty to disable).
  - Invalid text, keys used twice, and keys another app or Windows owns (Win32 error 1409) are logged and reported in one tray notification. The other hotkeys keep working.
  - Re-registered whenever the settings change. The tray menu shows the active Region and Full Screen hotkeys.
- **Live settings reload**: `settings.json` is watched (debounced), so edits made through Settings… apply on save. An edit that can't be read is logged and left alone, never overwritten.
- `RegionSelectionModel` window-only mode; `DropShadow` (box-blur ×3); `IWindowService.GetWindow` / `IsOccluded`.

### Tested
- 218 automated tests (217 pass, 1 explicit). New:
  - hotkey text parsing/formatting;
  - live-reload behaviour (external edit, invalid edit, own writes, opt-out);
  - drop-shadow geometry;
  - window-only picking;
  - window-capture backend policy and fallbacks with fakes;
  - registrar logic (taken, invalid, duplicate, disabled, live re-apply).
- New interactive tests:
  - `RegisterHotKey` round trip via `SendInput`, and a real conflict between two registrations;
  - WGC capture of a covered test window: the image is exactly its visible frame, the pattern under the cover is reproduced (≥ 99.5% exact), the cover really was on screen, shadow works, and minimized windows error.
- Manual on the dev machine:
  - all four hotkeys from another app's foreground: Alt+PrintScreen captured the Notepad window at exactly its frame (1426×575);
  - Esc closes the PrintScreen overlay;
  - a live edit to `Win+E` was reported as taken (1409) while the other three stayed registered;
  - the Capture Window picker captured the clicked window.

### Known limitations
- Hotkeys are edited in `settings.json` until the settings window (Phase 4).
- Picking a window still uses the frozen-screen overlay, so it is limited to top-level windows. Child controls and menus are Phase 5+ material.
- On Windows 10, capturing a covered window with WGC briefly shows the yellow border around that window.
- The synthesized shadow is a uniform soft shadow, not the exact DWM shadow.

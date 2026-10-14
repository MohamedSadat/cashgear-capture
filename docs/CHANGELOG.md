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

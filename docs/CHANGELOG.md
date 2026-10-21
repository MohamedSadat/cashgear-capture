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

## Phase 4 — Settings window, after-capture actions, file-name templates (2026-10-15)

### Added
- **After-capture actions** (`afterCapture`): save to file ("auto-save") and/or copy to the clipboard. "Open in editor" is present but disabled until Phase 5. If neither save nor copy is on, the user is told instead of the capture being thrown away.
- **File-name templates** (`output.fileNameTemplate`, Core `FileNameTemplate`):
  - Tokens: `{yyyy} {MM} {dd} {HH} {mm} {ss} {counter} {window} {monitor}` per the spec, plus `{kind}`.
  - Validation: unknown tokens, unbalanced braces, blank.
  - Sanitizing: invalid characters, doubled separators left by empty tokens, reserved device names, length cap.
  - `{counter}` is persistent, stored in `%LocalAppData%`, and only advances when the template uses it.
  - Several files from one full-screen capture get `_{monitor}` appended when the template lacks it.
  - The default template keeps the old naming style: `Capture_<date>_<time>_<kind>`.
- **Capture delay** (`capture.delaySeconds`): announced with a notification. Every capture also hides the app's own windows (e.g. Settings) and shows them again afterwards (spec §5.6).
- **Settings window** (MVVM, CommunityToolkit), Output / Capture / Hotkeys / General tabs:
  - Folder with Browse/Open; format and JPEG quality; template with a live example and token help.
  - Capture delay, hide delay, cursor, window shadow, full-screen output, capture method.
  - **Hotkey recorder:** global hotkeys pause while a box has focus, so PrintScreen can be recorded. Each row shows Active, why the key is taken or invalid, or "Applies when you save".
  - **Start with Windows** via `HKCU\…\Run`; the registry is the source of truth, so a change in Task Manager is reflected.
  - **Theme:** System / Light / Dark through WPF's Fluent `ThemeMode`, with the matching DWM dark title bar.
  - OK / Cancel / Apply and Reset to defaults. Validation blocks saving bad templates, relative folders, no after-capture action, duplicate or invalid hotkeys, and single typing keys as global hotkeys (also refused by the registrar).
- **Settings schema v2** with a JSON-level migration (`output.copyToClipboard` → `afterCapture.copyToClipboard`). An older file is rewritten once in the new shape. The file is written readably (`Ctrl+PrintScreen`, not `Ctrl\u002BPrintScreen`), and computed values are not stored.

### Tested
- 288+ automated tests. New:
  - template expansion, validation and sanitizing;
  - schema migration (including on load) and the persistent counter;
  - settings view model: load, preview, every validation rule, OK/Apply/Cancel/Reset, Browse, hotkey status after saving, pause/resume while recording;
  - registrar suspend/resume and the unsafe-key rule;
  - Start with Windows against a scratch registry key;
  - the readable-JSON regression.
- Manual on the dev machine:
  - the real v1 settings file migrated to v2;
  - Settings opened from the tray: changed the folder, JPEG, template and hotkey and turned on Start with Windows → Apply saved them, re-registered the hotkeys and wrote the Run key;
  - PrintScreen recorded in the hotkey box without taking a screenshot;
  - Dark theme applied live, with a dark title bar;
  - after a restart the saved Ctrl+Shift+F9 produced `Shot_0001_fullscreen_all.jpg`, `Shot_0002_monitor_DISPLAY2.jpg`, `Shot_0003_monitor_DISPLAY1.jpg` in the chosen folder;
  - a capture taken with Settings open did not contain the window, and the window came back;
  - Reset + Start with Windows off + OK restored the defaults and removed the Run key.

### Known limitations
- Open-in-editor is disabled until the editor exists (Phase 5); the spec's default ("open editor") becomes the default then.
- The capture delay shows a notification, not an on-screen countdown (Phase 9).
- The template has no sub-folder support (`/` and `\` are replaced).
- `ThemeMode` is marked experimental in .NET 10 WPF (diagnostic WPF0001 suppressed); the tray menu keeps the classic style.

## Phase 5 — Editor (2026-10-15)

### Added
- **Editor window** (`EditorWindow`, SkiaSharp `SKElement`): toolbar, properties panel and status bar.
  - Every capture opens in its own editor when **Open in the editor** is on. That is now the default after-capture action, per the spec. The editor's title shows the saved file's name, if any.
  - The canvas draws in physical pixels. At 100% one image pixel is one screen pixel, and the screen shows exactly the pixels that get exported (tested pixel for pixel).
- **Annotations** in `CashGear.Capture.Editor` (no WPF): arrow, line, rectangle, ellipse, freehand pen, highlight, text, step number, blur and pixelate.
  - Each kind has `Render(SKCanvas)`, `HitTest(SKPoint)`, `Bounds`, grips for resizing, and a transform for whole-image edits.
  - Annotations are immutable records with a stable id. Editing is non-destructive: they stay vector objects until save or copy flattens them.
- **Properties panel**:
  - Settings: colour palette plus hex, fill, line width, opacity, font family, size and bold, blur/pixel strength.
  - It edits the selected annotation, or the style for the next one. The last style used is remembered per tool (for example, yellow highlighter, red arrows).
- **Tools**:
  - Select, move, resize and delete, nudge with the arrow keys, bring to front / send to back.
  - Crop with an adjustable frame.
  - **Image** menu: rotate 90° left/right, resize (keeping proportions, or 25/50/200%), border (thickness, colour), drop shadow (the same shadow as window captures).
- **Undo/redo** through commands (`IEditorCommand` with `Do`/`Undo`): add, delete, change, reorder, and whole-image edits that keep the annotations aligned.
  - Slider drags and arrow-key nudges merge into one step.
  - Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z.
- **Zoom and pan**:
  - Zoom: Ctrl+wheel at the mouse, Ctrl+=/−, Fit (Ctrl+0; never enlarges past 100%), 100% (Ctrl+1).
  - Pan: wheel and Shift+wheel, middle-drag, Space+drag.
  - Fitting follows window resizes until you zoom yourself.
- **Output**:
  - Ctrl+S overwrites the capture's own file in its format. The new file is written to a temporary file first, so a failed save never damages the old one (`IOutputService.SaveToFileAsync`). An unsaved capture asks where to save, suggesting the capture folder and a template name.
  - Ctrl+Shift+S saves as PNG, JPEG or BMP, chosen by the extension.
  - Ctrl+C copies the flattened image (PNG, DIB, bitmap).
- **Closing safely**: closing an editor with unsaved changes asks Save / Don't save / Cancel. Tray › Exit and `--exit` close every editor first, and choosing Cancel keeps the app running.

### Deviations
- **Blur and pixelate** blur the screenshot only. They are always drawn beneath the other annotations, so they never hide an arrow or a label: they hide content, not marks.
- **After-capture default**: the spec's "open editor" default only applies to new or reset settings. A version 1 settings file is migrated to keep doing what it did (save, copy, no editor). Existing version 2 files keep their explicit values: turn on **Open in the editor** in Settings.
- **Annotations left outside a crop** are removed with it (undo brings them back).
- **Text** stays upright when the image is rotated (its box moves with the content).
- **Package**: `SkiaSharp.Views.WPF` brings OpenTK assemblies (for its OpenGL element), even though the editor uses the CPU `SKElement`.

### Fixed during testing
- A blur drawn over a shape covered the shape's edge.
- After a capture, an older editor came back on top of the new one.
- A narrow editor window wrapped its toolbar onto two rows.

### Tested
- 399 automated tests (398 pass, 1 explicit). New:
  - Editor (98):
    - hit-testing and grips for every annotation kind;
    - undo/redo, merging, the saved marker and image ownership;
    - crop, rotate, resize, border and shadow, applied to pixels and annotations;
    - rendering: opacity, highlighter, blur, pixelate, text, effects under shapes;
    - the export matching the 100% view pixel for pixel;
    - viewport zoom, fit and pan;
    - editing session gestures: draw, move, resize, Esc, text, step numbers, crop, styles, nudge, z-order, cursors.
  - Editor view model (13): save over the capture file, first save and Save As, cancel, close prompts, copy, panel styles, tool memory, crop.
  - The version-1 migration keeps auto-save and the clipboard on, without the editor.
- Manual on the dev machine:
  - a full-screen and a region capture opened in the editor;
  - drew every annotation kind with the toolbar keys;
  - moved and undid; changed zoom (100%, Ctrl+wheel, Fit, Ctrl+0/1) and panned (wheel, middle-drag, Space+drag);
  - cropped (Enter), rotated, added a shadow, resized to 593 × 356 and added a border, undoing each;
  - Ctrl+S replaced the auto-saved PNG with the edited 1186 × 711 image, leaving no temporary file;
  - Save As wrote a real JPEG;
  - Ctrl+C put the 1186 × 711 image on the clipboard;
  - typed two-line text, finished it by clicking away, then edited it with a double-click;
  - closing with changes and `--exit` both asked first: Cancel kept the window, No discarded the changes and exited;
  - a capture taken with an editor open didn't include that editor, and the new editor opened in front.

### Known limitations
- One selection at a time; annotations can't be copied or pasted.
- While text is typed, a WPF text box stands in for the final rendering, so line spacing may shift slightly when typing ends.
- Undo history isn't capped. Each whole-image edit keeps a copy of the image (about 14 MB for 3200 × 1082).
- Remembered tool styles last until the app exits; they are not saved in `settings.json`.
- Tilt-wheel (horizontal wheel) scrolling isn't supported; use Shift+wheel.
- Editors don't switch theme while open; theme changes apply to editors opened afterwards.

## Phase 6 — Scrolling capture (2026-10-15)

### Added
- **Scrolling capture** (tray › Scrolling Capture, `Ctrl+Shift+PrintScreen`, `--capture-scrolling`).
  - Pick a window on the overlay, clicking the part that scrolls. The window is brought to the front and captured, scrolled, left to settle and captured again, down to the bottom. The stitched image then goes through the usual after-capture actions (editor, save as `…_scrolling`, clipboard).
- **Scrolling target** (`ScrollTargetFinder`):
  - From the clicked point, UI Automation is walked up to the first element with a vertical `ScrollPattern`, and that element's rectangle is captured. In Notepad it's the "Text Editor" document.
  - If there is none, the window's client area is captured and the mouse wheel is used (Chrome here).
- **Scrolling**:
  - `ScrollPattern.SetScrollPercent` moves about 60% of a view per step and reports the bottom itself. If it fails, or claims to scroll while nothing moves, the mouse wheel takes over.
  - The wheel (SendInput, over the area's right edge to avoid hover effects) starts with one notch, measures how far a notch scrolls, then turns enough notches for about 60% of a view. The pointer is put back afterwards.
- **Settling**: after each scroll, a pause (`capture.scrollSettleMs`, default 250 ms). A frame is then taken once two captures 80 ms apart agree, so smooth scrolling and repaints have finished. The cursor is never drawn into the frames.
- **Stitching** in Core (`RowSignature`, `Overlap`, `ScrollStitcher`):
  - Every row gets a 64-bit fingerprint. The shift between two frames is the one where the most detailed rows agree and the fewest disagree, so repeated lines can't win over the true shift. Blank rows never decide, and a band on the right (the scrollbar) is ignored.
  - Fixed bands (toolbars above, status bars below) appear once: the top from the first frame, the bottom from the last.
  - A scrollbar thumb that moves between frames is cut off the result.
- **Stops**:
  - Normal ends: the bottom (nothing moved), **Finish now**, the height limit (`capture.scrollMaxHeight`, default 20,000 px; 300 frames at most), a frame that doesn't line up, or a resized window. Anything unusual is explained in a notification.
  - **Esc** (polled globally, since the scrolled window has the focus) or **Cancel** abandons the capture.
- **Progress window**: shows the parts and height so far, and which method scrolls. It is topmost, never takes the focus, is left out of every capture (`WDA_EXCLUDEFROMCAPTURE`), and sits in a corner of the monitor away from the wheel point.
- Settings › Capture › Scrolling capture: wait after each scroll, and the height limit. `{kind}` gains `scrolling`.

### Deviations
- **No OpenCV**: frames are lined up by exact row fingerprints in managed code, not OpenCvSharp `MatchTemplate`. Screen captures are pixel-exact, so exact matching gives the scroll distance to the pixel, without the duplicated or missing rows a best-correlation match can produce. It is fast (about 1 million row comparisons per frame) and avoids a large native dependency. Template matching would only help for blurry or rescaled input, which a screen capture never is.
- **Fixed bands**: detected from where the scrolled content stops matching. A misjudged blank row can't shift the result, because the same bottom band is applied to every frame.
- **Test fixtures** are generated in code (a deterministic synthetic page with text-like lines, repeated lines, toolbar, status bar and moving scrollbar), not stored as images in `tests/Fixtures`.
- **Scroll step**: about 60% of the view per step (the spec says about 70%), for more overlap.
- **Starting point**: the capture starts from the current scroll position; it does not scroll to the top first.

### Tested
- 422 automated tests (421 pass, 1 explicit). New:
  - stitcher: 70% steps, uneven steps and a short last step, toolbar + status bar + scrollbar, repeated lines, line-aligned scrolling with blank rows, caret blink, too far a scroll, resize, blank frames, metadata, fingerprints, offset search;
  - capture loop with a fake window: to the bottom, a scroller that knows the end, Finish now, height limit, fallback when automation doesn't move, losing track, settling during smooth scrolling, cancel;
  - wheel calibration;
  - `--capture-scrolling` and the `scrolling` file-name kind.
- Manual on the dev machine:
  - Notepad with a 240-bar test document: UI Automation scrolling, 25 frames, 8,664 px. Every bar in order, every bar 18 rows, constant 36-row spacing (nothing duplicated or missing), scrollbar cut off.
  - Chrome (guest window) with a 240-bar test page and a sticky header: the client area with the mouse wheel (100 px per notch measured, then 400 px steps), 14 frames, 5,423 px. Every bar in order, every bar 12 rows, constant 22-row spacing. Browser toolbar and sticky header once at the top.
  - The progress window was up during the capture and excluded from capture (affinity 0x11). Esc cancelled with nothing saved, and the window closed.

### Known limitations
- Vertical scrolling only.
- The window is captured from the screen, so it must stay visible while it scrolls.
- With the mouse wheel the whole client area is captured, so a browser's toolbar is included (once, at the top). The pointer moves during the capture.
- Content that changes while scrolling (animations, lazy loading, elements that appear only after scrolling) can stop the capture early ("didn't line up") or show up in more than one part.
- Frames are kept in memory until they are stitched (bounded by the height limit).

## Phase 7 — Screen recording (2026-10-15)

### Added
- **Screen recording to MP4** (tray › Record Screen…, `Shift+PrintScreen`, `--record`; the same again stops it).
  - Pick an area on the overlay: drag, or click a window or monitor, then Enter. Recording runs until Stop.
  - The file goes to the capture folder, named by the template (`{kind}` = `recording`), as `.mp4`.
- **Capture** (`WgcAreaStream`):
  - A continuous Windows.Graphics.Capture session on the area's monitor, with its own D3D11 device, so screenshots taken meanwhile don't wait.
  - Each frame Windows delivers is cropped on the GPU (`CopySubresourceRegion` into a staging texture the size of the area) and kept as "the latest picture".
  - The pointer is included unless turned off.
- **Encoding** (`FfmpegProcess`): raw BGRA frames go to `ffmpeg.exe` on standard input, and come out as H.264 (`yuv420p`, a keyframe every 2 s) in an MP4 with `+faststart`.
- **Encoder choice** (`FfmpegService`): NVENC, then Quick Sync, then AMF, then x264. Each is tested once with a real 0.2 s encode, since listed doesn't mean working. One quality number (x264 CRF scale) maps to each encoder's constant-quality mode.
- **Constant frame rate** (`FramePacer`):
  - A writer thread writes a frame every 1/fps of recording time, repeating the picture when the screen hasn't changed. The system timer is set to 1 ms while recording.
  - If ffmpeg falls more than half a second behind, the missed frames are skipped and counted.
  - **Pause** stops the clock, so the video continues seamlessly after it.
- **Click highlights** (`ClickHighlighter`): a translucent yellow (left) or red (right) circle under the pointer while a button is down, fading over 350 ms. Clicks shorter than a frame still show.
- **Recording toolbar**: elapsed time, Pause/Resume and Stop. It is excluded from capture, so it is never in the video or in screenshots. It never takes the focus, sits at the top centre of the monitor, and can be dragged.
- **Saved notification**: name, duration, size and encoder, with a warning if more than 5% of frames were skipped. Exiting the app first stops and saves a running recording.
- **Settings › Recording**: 15/30/60 fps, quality 15–35 (default 23), encoder (hardware when available / software), pointer, click highlights, and where ffmpeg is or what is missing.
- **ffmpeg**:
  - Looked for in `ffmpeg\` next to the app (then on the PATH). If it's missing, recording explains where to put it, and the rest of the app works.
  - `tools\get-ffmpeg.ps1` downloads a pinned build (Gyan "essentials" 9.0.2), checks its SHA-256, and keeps ffmpeg.exe, ffprobe.exe and the LICENSE in the git-ignored `tools\ffmpeg`. The build copies them next to the app and the App tests.

### Deviations
- **No FFMpegCore**: ffmpeg is started directly with `Process` and fed on standard input.
  - Its pipe source uses named pipes and pulls frames from an enumerator.
  - The recorder needs the opposite: it pushes frames on its own clock and uses the blocking write to notice when encoding falls behind.
  - Encoder probing and a clean finish (close stdin, wait for the MP4 to be finalized) are a few lines either way.
- **ffmpeg isn't committed**: the build used is GPL, because it includes x264. How it ships with the app is a packaging decision (Phase 9).
- **No audio**: optional in the spec.

### Fixed during development
- A failed start left the recorder "recording" with ffmpeg waiting for frames; a failed start now cleans up after itself.
- The interactive tests that capture the screen now run one at a time. A recording test's Windows 10 capture border landed in the overlay test's pixel comparison.

### Tested
- 449 automated tests (448 pass, 1 explicit). New:
  - frame pacing: grid, catching up, dropping when far behind, a minute of steady writing = 1,800 frames;
  - ffmpeg arguments for each encoder, even sizes, quality clamp, probe;
  - click highlight drawing, fading, short clicks, right button, edges;
  - recording settings, hotkey and file-name kind, `--record`;
  - ffmpeg service with the real ffmpeg: Software gives x264, Auto gives a working encoder, missing ffmpeg;
  - real recordings: 2 s at 30 fps gives H.264 320×240 with about 60 frames (ffprobe agrees with the count), pause left out, double start and stop refused;
  - notification text; settings round trip.
- Manual on the dev machine (GTX 1650 with driver 457.51, so NVENC fails — "Cannot load cuMemAllocAsync" — and so do Quick Sync and AMF; x264 was chosen):
  - 60 s of the whole primary monitor at 1920×1080, 30 fps, with a full-screen animation changing every frame: 1,853 frames, **0 dropped**, 1 repeated, constant 30 fps, H.264 High, 61.8 s, 15.8 MB (about 2 Mbit/s).
  - The app's memory stayed at 212–239 MB private for the whole minute.
  - The toolbar was visible, excluded from capture, and absent from the video.
  - A region recording with three clicks showed the highlight under the pointer; the toolbar's Stop button saved it (7.1 s, 214 frames, 0 dropped).

### Known limitations
- One monitor per recording. On Windows 10 Windows draws a yellow border around the recorded monitor (it can only be turned off on Windows 11).
- No audio.
- Hardware encoders are untested here, since none works on this PC. They are only used after a successful test encode, so a failing one falls back to x264.
- x264 uses the CPU. At 60 fps on large areas a slow PC may skip frames; the notification says so.
- The click highlight samples the mouse once per frame.

## Phase 8 — Capture Text (OCR) (2026-10-15)

### Added
- **Capture Text** (tray › Capture Text, `Ctrl+Alt+PrintScreen`, `--capture-text`). You select an area on the usual overlay (drag, or click a window or monitor, then Enter). The text in it is recognized with Windows OCR (`Windows.Media.Ocr`) and copied to the clipboard as Unicode text. A notification shows the first lines, the line count and the language used. An area with no text gives a warning, and the clipboard is left alone. Capture Text ignores the after-capture actions: no image is saved or opened.
- **Text layout**: lines are joined with line breaks, and a gap taller than about one line starts a new paragraph (blank line).
- **Right-to-left lines**: Windows OCR returns the words of an Arabic or Hebrew line in on-screen left-to-right order. The words are put back into reading order (right to left), and runs of English words or numbers inside the line keep their own order.
- **Languages** (Settings › General › Capture Text (OCR)): **Automatic** (default), or any language Windows has OCR for (`OcrEngine.AvailableRecognizerLanguages`). If a chosen language is later removed from Windows, it stays selected and shows as "(not installed)", and Capture Text says how to add it back.
- **Automatic**:
  - Reads the area once for each of your Windows display languages that has OCR installed (exact tag, else the same base language, e.g. `ar-EG` → `ar-SA`), up to three.
  - Each line written in a language's own script (Arabic, Hebrew, Cyrillic, Greek, Asian scripts) is taken from that language's engine. The other lines come from your first Latin-script language. Lines are matched by position.
  - So a mixed English/Arabic area comes out right in both languages, which no single Windows OCR language manages.
- **Small areas are read at 2×** (bilinear) when that stays within Windows OCR's limits (10,000 px side, about 2 megapixels before scaling). Large ones are scaled down to fit. Word positions are reported in the original pixels.
- `IOutputService.CopyTextToClipboardAsync` uses the same retry as image copies when another app holds the clipboard.

### Deviations
- **Automatic language mode** in addition to the language list the spec asks for. A single Windows OCR language can't read English and Arabic together: the English engine skips the Arabic, and the Arabic engine misreads Latin letters ("P1ease", ".nday"). The spec's acceptance ("a region of English and Arabic text") needs both.
- The default hotkey is `Ctrl+Alt+PrintScreen`, the free PrintScreen combination. The spec doesn't name one.

### Fixed during development
- Arabic lines came out in reverse word order (Windows gives visual order). Now in reading order.
- Automatic first chose one whole result, so a mixed area got the English lines from the Arabic engine, with errors. It now merges line by line.

### Tested
- 478 automated tests (477 pass, 1 explicit). New:
  - line and paragraph assembly;
  - right-to-left word order, with English inside an Arabic line;
  - automatic merging: Arabic only, English only, mixed lines, Arabic as the main language;
  - script per language tag, notification preview, bilinear scaler, OCR settings, the hotkey and `--capture-text`;
  - real Windows OCR on rendered text: English (2 lines), Arabic (`ar-SA`, reading order), Automatic picking the right language, a blank image is empty, an uninstalled language is refused with a hint, scale rules.
- Manual on the dev machine: Windows OCR has `ar-SA`, `de-DE` and `en-US` installed, and the display languages are en-US, ar-EG and de-DE. The sample was two English lines and two Arabic lines in Notepad, captured with `--capture-text` and Automatic:
  - English area: exact.
  - Arabic area: correct words in reading order. One letter differs: "إعلاق" for "إغلاق" (a dot the Arabic engine misses).
  - Both: English exact, a blank line, then the Arabic as above (182 characters, reported as English + Arabic).
  - Hotkey `Ctrl+Alt+PrintScreen` registered with the other five.

### Known limitations
- Quality is Windows OCR's. Arabic dots are sometimes misread, as above. Handwriting and vertical text aren't supported.
- Something drawn across a word, such as a blinking text cursor, can spoil that word. Here Notepad's cursor beside "Monday." gave "I%nday.l". So can an area edge that cuts into a word: leave a small margin.
- An Arabic line containing English words is read by the Arabic engine, which is weaker on the English words.
- Only languages installed for Windows OCR can be used. Automatic tries at most three of your display languages.
- No layout beyond lines and paragraphs: columns and tables come out line by line, top to bottom.

## Phase 9 — Polish + packaging (2026-10-15)

### Added
- **Capture history**:
  - The last 20 captures are kept, newest first (Settings › General: off, 10, 20, 50 or 100), including ones that were only copied or opened in the editor. Each is a PNG copy (or a file copy of the PNG just saved) plus a 240×160 thumbnail, in `%LocalAppData%\CashGear\Capture\history`.
  - Tray › **Recent Captures** shows the last 8 with thumbnails; a click opens one in the editor. If it was saved and the file still exists, Ctrl+S updates that file.
  - Tray › **Capture History…** shows all of them, with Open in Editor, Copy, Show in Folder, Save As… (PNG copied, JPEG/BMP converted), Remove and Clear History. Enter, Delete, Ctrl+C and double-click also work.
  - Lowering the limit removes the oldest straight away. Files the list doesn't name, and entries whose file is gone, are cleaned up when the history is read.
- **Delayed capture**:
  - Tray › **Capture Delay**: no delay, 3, 5 or 10 seconds (Settings › Capture also offers 1, 2, 15 and 30). The menu shows the current delay.
  - During the delay a countdown sits in the bottom-right corner of the monitor with the pointer. It never takes the focus, so a menu or tooltip opened for the capture stays open. It is left out of captures and has a **Cancel** button. It replaces the "Capturing in N seconds" balloon.
- **Theme follows Windows while running**: with "Use the Windows setting", open windows switch when Windows switches between light and dark apps. Changing the theme setting re-themes every open window, not just Settings. Title bars and the editor's canvas follow too.
- **Clickable notifications**: clicking "Capture saved" or "Recording saved" shows the file in Explorer. Clicking "Update ready" restarts into the update. The notification says "(click to show)".
- **App icon**: a blue rounded square with white viewfinder corners, drawn by `tools\make-icon.ps1` at 16–256 px (the small sizes pixel-snapped). It is used by the exe, every window, the tray (at the notification area's size instead of the generated "CG"), the installer and Apps & Features.
- **Installer** (Velopack 1.2.161):
  - `tools\package.ps1` publishes the app self-contained for win-x64 as a single-file exe. WPF's and SkiaSharp's native DLLs sit beside it, with `ffmpeg\` and `THIRD-PARTY-NOTICES.txt`.
  - It then packs `CashGear.Capture-win-Setup.exe`, a portable zip, and full + delta update packages with `releases.win.json`.
  - Setup installs per user, with no admin rights, to `%LocalAppData%\CashGear.Capture`. It adds Start menu and desktop shortcuts and an Apps & Features entry, then starts the app. The first start shows where the tray icon is and the region hotkey.
- **Updates**:
  - Installed copies check a release feed: an https address or a folder, set when packaging (`-UpdateFeedUrl`) or by `updates.feedUrl` in settings.json.
  - The first check runs a minute after start, then daily; Settings › General can turn this off, and its **Check now** button checks immediately.
  - A newer version downloads in the background (as a delta when possible). The tray then offers **Restart to Update (x.y.z)**. Otherwise it's installed the next time the app starts, and after the restart a notification says so.
  - Settings shows the version and the last result.
- **Code-signing hook**: `-SignParams` (signtool arguments), `-SignTemplate` (any command) or `-AzureTrustedSignFile`, also read from `CASHGEAR_SIGN_PARAMS`, `CASHGEAR_SIGN_TEMPLATE` or `CASHGEAR_AZURE_SIGN_FILE`. Velopack then signs the app's binaries, the updater and Setup.exe. Without one, the build warns that it is unsigned.
- **Uninstall** (Apps & Features):
  - The running copy is asked to exit: a recording is saved, and editors ask about unsaved changes. Whatever still runs from the installation after 20 s is stopped.
  - The start-with-Windows entry is removed, but only if it starts the uninstalled copy.
  - Settings, logs, history and captures are kept.
- Version **1.0.0**.

### Fixed during development
- **Two captures could run at once.** A command-line switch or hotkey arriving during another capture's countdown or selection opened a second overlay on top of the first, because `ExecuteAsync` ignores the commands' disabled state. Now one capture runs at a time, and a request in the meantime is ignored and logged. The region selector also never opens a second overlay, which covers a recording's area pick too.
- **Uninstalling left the app running** with its files half removed. Velopack's own check for running copies didn't recognise it ("0 matched directory" with the app running from `current\`). The uninstall hook now stops it (see above).
- The history store deleted the files it had just written the first time it was used: reading the list removes unlisted files. Found by the unit tests.
- **The installed app could start without a tray icon** and look as if it never opened. H.NotifyIcon names the icon after the assembly, so a development build and the installed copy shared one icon id, and Windows refuses an id from a second program path while the first file exists. The tray icon now has an id per program file (stable across updates, since the installed copy always runs from `current\`). If Windows still refuses the icon (e.g. the taskbar isn't up yet at sign-in), it retries every 2 s for 2 minutes and says so if it never succeeds; notifications wait for the icon. A failure during start-up now shows a message and exits, instead of leaving a half-started app with no icon, no hotkeys and a hand-off pipe that never answers.
- "Show in folder" uses the shell's `SHOpenFolderAndSelectItems`, which reuses an open window on the folder, instead of starting `explorer /select`.

### Deviations
- **Velopack rather than MSIX.** MSIX can't be installed without a trusted signing certificate, which would mean adding a certificate to the machine's trust store. Velopack installs per user without admin rights, works unsigned until a certificate is supplied, and brings delta updates (the spec allows either).
- **"Single file where possible"**: the app is one exe (152 MB: managed code and the .NET runtime). WPF's and SkiaSharp's native DLLs (about 20 MB) stay next to it, rather than being unpacked to `%TEMP%` on every start. ffmpeg is a separate program.
- **Notifications are the tray icon's.** Windows 10/11 show them as toasts, and clicks come back through the icon without the app registering a toast identity.
- **ffmpeg ships in the installer**: a GPL build, run as a separate program, with its licence and a notice. `-NoFfmpeg` leaves it out. ffprobe, only used by the tests, is no longer copied next to the app, which saves 100 MB.

### Tested
- 513 automated tests (512 pass, 1 explicit). New:
  - history store: newest first, copying instead of re-encoding, trimming on add and when the limit is lowered, off, remove and clear, survives a restart, cleanup, broken or tampered list;
  - thumbnail sizes and the area-averaging shrink (alpha-weighted, strided);
  - PNG read back pixel for pixel; history actions: open with or without the saved file, Save As copy or convert, errors reported;
  - History window: selection kept, neighbour selected after removal, confirmed clear;
  - "when" labels;
  - update flow: unavailable without an installation or feed, settings feed wins, quiet automatic checks, download → ready → click → restart, failures, pending at start-up;
  - theme service: follows Windows and the setting;
  - uninstall removes only our own start-with-Windows entry;
  - settings: history size and update checks.
- Manual on this PC (Windows 10 22H2, not a fresh machine; see TESTING.md):
  - Built Setup.exe; installed 1.0.0 and confirmed the first-run start, shortcuts and Apps & Features entry.
  - Tray menu, Capture Delay and the countdown (3-2-1, excluded from capture, never focused), Cancel, and a second request ignored.
  - Recent Captures and Capture History, and opening one in the editor.
  - Settings › General.
  - Update 1.0.0 → 1.0.1 through Check now and Restart to Update.
  - Start with Windows: the Run key points to the installed exe, and running that command starts the app.
  - Uninstall while running: the app exited in 0.1 s, nothing kept running, and the folder, shortcuts, entry and Run key were gone.
  - Clicking a "Capture saved" notification opened the capture folder.

### Known limitations
- Not yet installed on a fresh Windows 10 or on Windows 11: there is no Windows Sandbox or virtual machine on this PC. Starting at sign-in was checked by running the Run-key command, not by signing out. Steps are in TESTING.md.
- Unsigned until a certificate is supplied. A downloaded Setup.exe gets SmartScreen's "Windows protected your PC".
- No update address is built in by default: package with `-UpdateFeedUrl` and host the contents of `artifacts\releases` there.
- The download is about 118 MB and the installation about 275 MB, since .NET, WPF and ffmpeg are included.
- Uninstalling keeps user data: `%AppData%\CashGear\Capture`, `%LocalAppData%\CashGear\Capture` (logs, history) and saved captures. Velopack also leaves its logs in `%LocalAppData%\velopack`, and shows its own "Uninstall Complete" dialog.
- In the OneDrive-synced capture folder here, Explorer opened the folder but did not keep the new file selected (with `explorer /select` and with the shell call alike).
- Windows doesn't show notification banners while it considers a full-screen app to be in front ("busy"); they go to the Action Center instead.
- Theme switching with Windows was checked by unit tests (with a simulated Windows theme), not by switching this PC's theme.
- Recordings are not in the history.

## Sound in screen recordings (2026-10-15)

Lifts Phase 7's "No audio" limitation.

### Added
- **Sound in recordings**:
  - System audio ("what you hear": loopback of the default output device) is on by default.
  - A microphone can be added.
  - Both are recorded with the video and saved in the MP4 as AAC, 48 kHz stereo at 160 kbit/s, mixed into one track when both are on.
- **Settings › Recording › Sound**:
  - "Record system audio (what you hear)".
  - **Microphone**: None (default), "Default microphone (Windows setting)", or any connected microphone. A chosen microphone that isn't connected stays chosen and shows as not connected.
  - The tab says which device system audio comes from.
  - Stored as `recording.systemAudio` and `recording.microphone`. Older settings files get system audio on and no microphone.
- **Toolbar**:
  - A speaker and a level meter (−60 to 0 dB) while sound is recorded.
  - **Mute mic** / **Unmute mic** when a microphone is recorded.
  - A crossed-out speaker, with the reason as its tooltip, when sound was asked for but isn't available.
- **Saved notification**:
  - Says which sound the file has ("· system sound + microphone").
  - When sound that was asked for is missing, it says why and becomes a warning. The reasons are: no output device, the microphone isn't connected or Windows' privacy settings block it, or a device stopped during the recording.
  - With no usable sound device at all, the video is recorded without sound, as before.
- **Capture** (`WasapiAudioSource`):
  - WASAPI shared mode; Windows converts every device to 48 kHz stereo float (`AUTOCONVERTPCM`).
  - Polled every 10 ms on its own thread.
  - Each packet keeps Windows' timestamp (QPC) for its first frame. Silent packets become zeros, and a discontinuity puts the next packet at its timestamp.
- **Keeping sound and picture together**:
  - `RecordingClock` replaces the recorder's stopwatch. It maps any moment to recording time, so sound captured during a pause (or before the start, or after the stop) is cut out to the sample, and a packet that straddles Pause or Resume is split.
  - `AudioMixer` / `AudioTrack`: each source has a ring buffer indexed by recording sample, and packets go where their timestamps say.
    - A packet within 30 ms of following on from the previous one is placed right after it, so timestamp jitter never makes gaps.
    - Loopback sends nothing while nothing plays. Those stretches stay silent, and the next sound lands at its own time: **silence is filled by the clock**.
    - A sound card whose clock drifts against the PC's is pulled back one sample per packet once the smoothed drift passes 10 ms.
  - The sound is mixed 80 ms behind the clock, so packets that arrive a little late still make it, and written to ffmpeg by its own thread.
  - **Dropped frames:** for each video frame the pacer drops (only when encoding falls more than 0.5 s behind), the same 1/fps of sound is left out, so the two stay together after the jump.
  - **Stop:** the video is written up to the frame due at the stop, and the sound to exactly the same length (48000/fps samples per frame). The two streams are equally long; ffprobe shows the same duration for both.
- **ffmpeg**:
  - The sound is a second input: raw float on a named pipe (`\\.\pipe\cashgear-rec-<guid>`, current user only), passed as `-f f32le -ar 48000 -ch_layout stereo -i <pipe> -map 0:v:0 -map 1:a:0 … -c:a aac -b:a 160k`.
  - With no sound the command line is unchanged.
- **NAudio.Wasapi 3.1.0** (MIT) for the Windows Core Audio interop, added to `THIRD-PARTY-NOTICES.txt`.

### Deviations
- **A named pipe, not a temp file muxed at stop.**
  - Why:
    - one encode pass, so Stop is as quick as before;
    - no temporary sound file (about 1.4 GB an hour as float);
    - nothing left behind if the app is killed;
    - ffmpeg interleaves the streams itself.
  - The cost: ffmpeg (7 and later) stops reading whichever input runs more than 100 ms ahead of the other, so the two writers must never wait for each other:
    - The sound follows the clock only, never the video's progress. At 1080p ffmpeg probes one video frame and then waits for 85 ms of sound before it reads stdin again, so tying sound to video deadlocks.
    - A pause or a stop lets the sound catch up to the clock.
    - At Stop the sound pipe is closed without waiting for the video, and stdin without waiting for the sound.
- **Our own capture loop on NAudio's Core Audio interop, not NAudio's `WasapiCapture`.**
  - `WasapiCapture` drops the QPC timestamps and the discontinuity flag, and polls every 50 ms.
  - It posts its events to the creating thread's SynchronizationContext, and its capture thread can throw when a device disappears.
  - Raw WASAPI would have meant about 300 lines of COM interop that NAudio already provides.
- **"Default microphone (Windows setting)"** in addition to the list of devices, so a different headset doesn't need a trip to Settings.
- The only level control is the toolbar's mute; microphone volume is set in Windows.

### Fixed during development
- A packet delivered late was placed at its arrival time. The original check rejected Windows' timestamp when it was more than 200 ms from arrival. In one real-sound test run, a beep captured during a pause landed just after Resume. Windows' timestamp is now kept unless it is missing, more than 200 ms in the future, or more than 5 s old.
- Stop could freeze for good, in about 1 in 20 real-sound test runs. The screen capture was released while holding its lock; a frame arriving at that moment waited for the lock, and the release waited for the frame. This was there before sound too (since Phase 7). The capture is now released outside the lock, and Stop closes ffmpeg's video input before releasing it. See TESTING.md.

### Tested
- **546 automated tests** (544 pass, 2 explicit). New:
  - recording clock: pauses, sound mapped to recording samples, packets split at Pause and Resume, nothing from a pause, before the start or after the stop;
  - mixer:
    - jittery timestamps make no gaps;
    - no packets give silence of the clock's length;
    - sound after a silence lands at its time;
    - late sound is trimmed;
    - jumping back replaces instead of doubling;
    - ±0.05% sound card drift stays within 15 ms over ten minutes;
    - tracks summed and clamped; mute; frames skipped for dropped video; overflow counted; peak level;
  - sound frames per video frame, the mixing schedule, `FramePacer.Due`;
  - ffmpeg arguments with sound (and unchanged without); recording settings: defaults, microphone normalizing, a settings file from before sound;
  - Windows timestamps: used for late packets, rejected when missing, far in the future or very old; 100 ns conversion without overflow;
  - notification text with sound and with missing sound; toolbar: level meter scale, mute toggle, missing-sound tooltip; Settings: microphone list, a disconnected microphone kept, round trip;
  - real recordings with the real ffmpeg:
    - 3 s with sound: AAC 48 kHz stereo as long as the video, 0 frames dropped;
    - loopback-like tone bursts at 0.5 s and 3.0 s around a 1.5 s pause land at 0.5 s and 1.5 s (±50 ms);
    - sound asked for but unavailable gives a video-only file and a note;
    - real system audio with nothing playing still gives a full-length sound track.
  - releasing the screen capture while a frame's callback waits for its lock returns (it used to freeze Stop);
  - Explicit: real beeps played through the default device while recording 10 s with a pause land where they were played (±30 ms).
- **Manual on this PC** (see TESTING.md). The default output device is the ZOOM H5's headphone output, and the microphone is its input (USB).
  - 10 s of a region with beeps and a pause from the toolbar.
  - System audio + H5 microphone, with mute toggled from the toolbar.
  - Sound off.
  - 60 s of the whole 1920×1080 monitor with sound: 1,814 frames, 0 dropped, video and sound both 60.467 s, beeps exactly 1.000 s apart.

### Known limitations
- System audio comes from the device that was the default output when the recording started; switching output devices during a recording records silence from the old one.
- When encoding falls far behind and frames are dropped, the same span of sound is left out, so the sound skips with the picture.
- Hardware encoders are untested with sound here (none works on this PC).
- If Windows' privacy settings block desktop apps from the microphone, the microphone isn't recorded (the notification says how to allow it).

## 1.0.1 — Fixes after 1.0.0 (2026-10-17)

### Fixed
- **The tray icon could still be refused after installing**, for good, with "Windows did not let it add its icon" after 2 minutes. An earlier copy had been installed from a shell started by a packaged app (Claude's desktop app). Windows redirects that app's AppData writes, so the copy landed in `%LocalAppData%\Packages\<package>\LocalCache\Local\CashGear.Capture` while still seeing the usual path. Both copies therefore computed the same icon id, and Windows had tied it to the hidden copy's file. Restarting the real install didn't help, and neither did deleting the hidden copy. When the usual id is refused, the app now tries two fallback ids, each stable for its path. The usual id is unchanged, so existing installs keep their "always show this icon" choice.
- The message shown after 2 minutes of refusals no longer says a restart usually helps. It names the likely causes (no taskbar, or other copies holding the icon) and the log folder.
- **The app could not exit while that message was open.** It was a message box, which runs its own modal loop, so WPF could not finish exiting until OK was clicked. On 2026-10-16 an `--exit` logged "UI exiting", but the process stayed alive until the user clicked OK. A restart to update would have waited the same way, and uninstalling only got past it by stopping the app after 20 s. The message (same text, warning icon and OK button) is now a small window of the app, which the app closes when it exits.
- **A copy running as administrator ignored every launch that wasn't, without a word.** Starting the app again, `--exit` or a capture switch from a normal prompt did nothing (seen on 2026-10-16 while troubleshooting the tray icon). Later launches hand their command over through a pipe that .NET restricts to the token's default owner. For an elevated process that owner is Administrators, not the user, so only elevated processes can open its pipe. That stays: otherwise any program the user runs could make an elevated process capture, record or exit. Such a launch now shows a message saying the copy runs as administrator and how to use or close it, then exits with code 1. The log also notes when a copy starts as administrator.
- **An elevated launch couldn't reach a normal copy either.** `--exit` from an administrator prompt failed with "not owned by the current user", because .NET's own check expected the pipe to belong to Administrators. The launch now checks the pipe's owner itself: the user, or Administrators when the launch is elevated too. It connects for identification only, so the copy it reaches can't act as it.

### Tested
- **561 automated tests** (559 pass, 2 explicit). New:
  - the usual icon id is pinned to its 1.0.0 value; fallback ids differ from it, from each other and from another path's, and don't change with the path's case;
  - the message after the last refusal is shown while the UI thread exits the way the app does; the thread must finish within 10 s and the message must close (with a message box in its place, this test fails after 10 s);
  - that message names the log folder;
  - a command reaches a pipe created like the instance's;
  - nothing listening is "no answer";
  - a pipe that shuts the user out reads as a copy running as administrator;
  - which pipe owners a normal and an elevated launch accept;
  - the messages name the switch and say how to close the copy.
- **Manual on this PC**: the tray icon conflict reproduced with two copies of the development build at one path, one in Claude's virtualized AppData and one in the real AppData. The fix was confirmed on it (see TESTING.md).
- **Manual on this PC**: `--exit` while the tray icon message was open exited in about 1 s, with a temporary build that always gives up after 2 attempts (see TESTING.md).
- **Manual on this PC** (see TESTING.md). With the fixed build running as administrator, a normal start, `--exit` and `--capture-region` each showed the message and exited with code 1; 1.0.0's `--exit` exited silently. With a normal copy running, an elevated `--exit` closed it, where 1.0.0's failed.

### Known limitations
- Uninstalling can't close a copy that runs as administrator: Windows refuses both the exit request and ending the process (both are logged). Close it from its tray icon first.

## Phase 10 — Recording engine v2 (2026-10-17)

The first phase of the extended plan. Screen recording no longer runs `ffmpeg.exe`. FFmpeg now runs inside the app, every picture and sound packet is stamped with one clock, the file being recorded survives a crash, and an encoder that fails mid-recording is replaced.

### Added
- **FFmpeg inside the app** (new project `CashGear.Capture.Media`, FFmpeg.AutoGen 9.0.1.1):
  - The libraries come from an LGPL v3 shared build of FFmpeg 9.0.2 (BtbN, month-end autobuild, SHA-256 pinned in `tools\get-ffmpeg.ps1`).
  - Only `avutil`, `swresample`, `swscale`, `avcodec` and `avformat` ship, in `ffmpeg\`, with their licence texts. See [LICENSES.md](LICENSES.md) for the decision and the source.
  - `FfmpegRuntime` loads them on first use and refuses a library of the wrong major version. When FFmpeg is missing, recording says so and everything else works.
  - FFmpeg's log goes to the app log. A failed call says what FFmpeg said, e.g. "Cannot load cuMemAllocAsync" for NVENC with an old driver.
- **One clock for picture and sound.**
  - Each picture from Windows.Graphics.Capture keeps the time Windows captured it (`SystemRelativeTime`). That is the same QPC clock WASAPI stamps sound with, read through `RecordingClock`.
  - A `FrameGrid` turns those pictures into constant-frame-rate video:
    - a picture lands on the frame nearest its capture;
    - a frame is made 50 ms after its time, so late pictures still make it;
    - a still screen repeats the last picture;
    - a picture captured during a pause shows from the resume, so a change made while paused isn't lost.
  - If encoding falls far behind, frames are skipped and their timestamps left out (the picture holds still). The sound is never cut to match, so it stays in sync after the gap.
- **Pipeline.**
  - **Capture.** Each picture Windows sends is cropped on the GPU into a pooled texture and queued, one per video frame: a newer picture for the same frame replaces the older one.
  - **Video thread.** Reads back only the pictures it encodes, draws the click highlights, converts BGRA to NV12 with BT.709 limited-range colours (swscale's default would be BT.601), and encodes.
  - **Sound thread.** Encodes the mix and/or each source to AAC, 80 ms behind the clock.
  - **Writer thread.** Owns the file. The threads never wait for each other.
- **Encoders**, found by test-encoding eight 256×256 frames with each:
  - **Order for Automatic:** NVENC (H.264; HEVC and AV1 when chosen), Intel Quick Sync, AMD AMF, then Windows' Media Foundation with the GPU vendor's hardware encoder, then Media Foundation's software encoder (part of Windows), then OpenH264.
  - **Settings › Recording › Encoder** offers Automatic, Software only, or any encoder. The list shows which work here, and Media Foundation's hardware encoder is shown by name.
  - **Re-checking.** Results are cached in `%LocalAppData%\CashGear\Capture\encoders.json`, keyed by the FFmpeg build, every graphics adapter with its driver version, and the Windows build. **Check again** repeats the detection.
  - **Opening.** At Start the chosen encoder is opened at the real size; if that fails, the next one is tried.
- **Quality presets** Small / Balanced / High / Near lossless replace the 15–35 number, mapped to each encoder's own scale.
  - Media Foundation's values were measured on this PC by SSIM against the old x264 settings (CRF 30 / 23 / 18): hardware quality 35 / 50 / 65 / 90, software 55 / 72 / 88 / 100.
  - Detection checks that a Media Foundation encoder honours its quality setting, and gives it a bit rate if it doesn't.
- **Encoder failure mid-recording.**
  - The failed encoder's segment is finished, the next working encoder continues in a new segment, and the rest is saved as `name (part 2).mp4`.
  - A notification says so at the time, and the saved notification names both parts.
  - A 2 s watchdog catches an encoder call that never returns.
- **Crash-safe recording.**
  - Recordings are written as they happen to fragmented MP4 in `%LocalAppData%\CashGear\Capture\Recordings\Temp\<time>-<id>\`. A fragment is cut at least every second and every packet is flushed. A manifest records where the recording was going, and a lock file is held while it is written.
  - **Stop.** Each segment is copied (no re-encoding) into the final MP4, with the index at the front (`+faststart`). The file is written as `.partial` and renamed, then the temporary folder is removed. If saving fails, the folder is kept for recovery.
  - **Recovery.** A folder whose lock opens belongs to a recording the app never finished: closed, crashed, or the PC lost power. A few seconds after start-up a notification offers to save it, and so does tray › **Save Interrupted Recordings**. It is saved where it was going, up to about a second before the interruption. A file cut short is recognised by its missing fragment index.
- **Sound tracks.** With system audio and a microphone, Settings › Recording › With both offers:
  - one mixed track (the default, plays everywhere);
  - a track per source (for editors);
  - the mix followed by each source.

  Tracks are titled ("Mixed", "System audio", "Microphone"), and only the first is played by default. A single source is always one track.
- **Toolbar.** A sound button opens a panel inside the toolbar (so it stays out of the recording) with mute and a 0–200% volume for each source. The volumes are kept for the next recording.
- **An unplugged device** during a recording leaves silence on its tracks. A notification says so, and the recording goes on.
- **The display stays on while recording.** Like a video player, the recorder asks Windows not to turn the display off or sleep for lack of input (`SetThreadExecutionState`, held by the recording's video thread). When the display goes off, Windows stops drawing the screen, so a recording made without touching the mouse or keyboard froze when the power plan turned the display off.
- **Saving state.** The tray says "Saving the Recording…" while a stopped recording is saved, and a new recording waits for it.
- **Settings schema 3.**
  - `recording.quality` is a preset; a version 2 file's number becomes the nearest one: ≤17 Near lossless, 18–21 High, 22–26 Balanced, ≥27 Small.
  - `recording.encoder` is an id (`auto`, `software`, `h264_mf_hw`, …); "Auto" and "Software" carry over.
  - New `recording.tracks`, `recording.systemAudioGain` and `recording.microphoneGain`.
- **`tools\SyncTest`**, the acceptance test for recording:
  - A square flashes on the primary monitor at the start of each beep. The beeps are timed by the sound card's clock, and each flash is placed by that clock read against QPC.
  - The real recorder records the monitor; the file is then decoded, and each flash is paired with its beep.
  - It reports offset, spread, drift and CPU, records with a microphone and track layout when asked, and recovers and analyzes a run that was killed.
- **Uninstall** no longer looks for `ffmpeg` processes; there are none.

### Deviations from the spec
- **Temporary file: fragmented MP4, not MKV** (the spec allows either).
  - FFmpeg's Matroska muxer stores timestamps in milliseconds, so 30 fps frames would alternate 33/34 ms.
  - Fragmented MP4 keeps exact 1/fps and 1/48000 timestamps, and saving is a plain copy.
  - A kill loses about a second either way.
- **No zero-copy GPU path yet.**
  - Pictures are cropped on the GPU, and only those that are encoded are read back to the CPU. Click highlights and the BT.709 conversion run on the CPU.
  - With the GPU's encoder this used 1.9% of the CPU here (0.46 of 24 logical CPUs, every frame read back; below), so the D3D11 frames path (FFmpeg 9's `h264_mf` accepts D3D11 textures) is left for Phase 18's performance work.
- **More encoders than the spec lists.** The LGPL build has no x264, and NVENC/QSV/AMF don't work on this PC (the NVIDIA driver is too old for FFmpeg's NVENC). Windows' Media Foundation encoders are added between the vendors' encoders and OpenH264:
  - the GPU's encoder through Windows, which works here: NVIDIA H.264 Encoder MFT;
  - Microsoft's software encoder, licensed with Windows.
- **The detection test is 256×256**, not 64×64: some hardware encoders refuse very small pictures.
- **Detection results go to `encoders.json` in `%LocalAppData%`**, not the settings file. They describe this PC, and settings.json is roaming.
- **A second encoder means a second file.** Segments from different encoders have different codec headers (SPS/PPS), which one MP4 track can't hold. Each part starts at 0.
- **The `CashGear.Capture.Audio` project is left to Phase 11.**
  - The spec puts the WASAPI wrapper that exposes `qpcPosition` there. That wrapper already exists in Recording (the capture loop of "Sound in screen recordings").
  - Phase 11 brings the code that belongs in that project: device management, process loopback, noise suppression.
- **`RecordingClock` is the shared clock.** There is no separate `IMediaClock` interface: nothing else implements it.
- **AAC only.** Opus isn't offered, since MP4 players expect AAC.
- **Not done here:** turning off the yellow capture border needs Windows 11 (this PC is Windows 10 19045), and recovering a capture after the GPU is reset is left to Phase 18.

### Fixed during development
- **Lost pictures.**
  - The capture queue first held the newest 3 pictures. Windows sends a picture whenever anything on the monitor changes, here 60 a second, so the queue was refilled every 50 ms: exactly the frame grid's wait.
  - The picture a frame needed had always been dropped by the time it was due, and a change made during a pause never showed.
  - It now keeps one picture per frame.
- **A crash on encoder fallback.** An encoder switch read a property of the encoder it had just closed, and the exception escaped the video thread and ended the process (here, the test host). Recording threads now catch everything and stop the recording instead.
- **A cut-short file counted as complete.** FFmpeg's MP4 reader reports the end of a cut-short file as a normal end. The remuxer now also checks for the fragment index.
- **Lost track titles.** They come back from an MP4 as `name`, so they were lost in the copy to the final file.
- **First packet late.** Microsoft's software H.264 encoder holds back 16 frames (about 0.5 s) before its first packet. A segment's start is now taken from that packet, not from the frame counter.
- **The display turning off froze the recording.** The first 60-minute test froze at 29:38: the power plan turned the display off after 30 minutes without input, and Windows stopped drawing the screen. The recorder now keeps the display on while it records.
- **README.** It had a raw carriage return where `artifacts\releases` should be (since Phase 9).

### Tested
- **656 automated tests** (654 pass, 2 explicit). New:
  - Media (25):
    - loading and refusing the libraries, FFmpeg's error text, per-thread log capture;
    - every encoder that works here gives one decodable packet per frame;
    - BT.709 colours;
    - Media Foundation presets change the size;
    - AAC framing;
    - flashes on their exact frames and beeps within 2 ms through the temporary file and the remux;
    - 10 random truncations recovering to within 2 s of the cut;
    - titled tracks with one default;
    - Arabic and emoji paths;
    - a second part starting at 0;
    - an unwritable file reported.
  - Core:
    - encoder preferences and preset tables, the legacy quality map, the detection cache;
    - the recording store and part names;
    - the frame grid (nearest frame, the 50 ms wait, catching up, skipping as a gap, stopping, 1,800 frames a minute);
    - pictures during a pause;
    - the mixer's gain and per-source outputs, the track layouts;
    - settings schema 3 and migration.
  - App:
    - detection and its cache;
    - usable encoders in order;
    - recovery (whole, cut short, a taken name, a recording still being written, discard);
    - the interrupted-recording prompts;
    - Settings' encoder list and re-check;
    - the toolbar's sound panel;
    - both windows opened on the desktop.
  - Real recordings:
    - a change made during a pause shows after resume;
    - an unplugged microphone leaves silence and a warning;
    - an injected encoder failure gives two playable parts;
    - a stalled encoder skips frames but keeps all the sound;
    - temporary files are removed after saving;
    - all of Phase 7's and "Sound in recordings" recording tests, on the new engine.
- **Manual on this PC with SyncTest** (Windows 10 22H2, GTX 1650 with driver 457.51, Xeon E5-2680 v3 with 24 logical CPUs; Automatic picked the Windows hardware encoder, i.e. NVIDIA's MFT). See TESTING.md.
  - **Two 60-minute runs** at 1920×1080 and 30 fps, Automatic (NVIDIA MFT), system audio, 3 pauses of 5 s. Each file is 3,600.03 s with 108,001 frames, 0 dropped, and every beep (1,800 / 1,801).
    - **Run 1.** The first 29:38 was paired: 890 claps, drift −4.8 ms on the fitted line and −4.9 ms from the first 5 minutes to the last, offset −44.9 ms (sd 5.2).
      - Then the power plan turned the display off (30 minutes without input), Windows stopped drawing the screen, and the video froze.
      - Fixed: the recorder now keeps the display on.
    - **Run 2, with the fix.** The display was still asleep at the start and came back at 22:50.
      - From there to the end (1,116 claps) the offset was −40.2 ms in every 10-minute stretch: drift 0.0 ms at the end of the hour.
      - CPU 1.6% (0.39 cores).
  - **10 minutes with the software encoder** (Microsoft's H264 Encoder MFT) and a square repainting every frame: 18,001 frames, 0 dropped, 300 of 300 claps.
    - Every offset is −82 or −49 ms, one frame apart, and the first and last minutes are both −82.0 ms: no drift.
    - The fitted line says −10.5 ms only because a run of flashes landed a frame early in minutes 1–4.
  - **Killed with Stop-Process five times and recovered:**
    - after 12 s → 11.0 s saved;
    - after 25 s → 24.0 s;
    - after 1.5 s → 1.0 s;
    - during a pause at 10 s of recording → 9.0 s;
    - at 60 fps after 40 s → 39.0 s.

    Every file was complete and decoded without errors, and each kept its sync (offsets −32 to −17 ms, drift under 1 frame).
  - **ZOOM H5 unplugged from USB** 30 s into a 3-minute recording of system audio and its microphone (track layout Both). Both sources reported once; the recording ran its full 180.03 s with 0 frames dropped. All three tracks (Mixed, System audio, Microphone) are 180.03 s long and silent (−91 dB) after the unplug.
  - **CPU at 1920×1080, 30 fps,** with a square repainting every frame, so Windows sends a new picture every frame and each one is read back. Measured for the whole SyncTest process, including its own drawing:
    - GPU encoder (NVIDIA MFT): 1.9% of 24 logical CPUs (0.46 cores) over 5 minutes, 9,001 frames, 0 dropped.
    - Microsoft's software encoder: 4.1% (0.99 cores).
    - On a normal desktop the hour-long runs used 1.5–1.6%.
    - The target is under 10% (2.4 cores here).
  - **The offset between beep and flash is constant within a run** (−34 to −82 ms across runs). It is the time the flash takes to reach the screen (WPF, then DWM, a frame or two) against the sound card's output delay, which differs between the H5 and the PC's speakers and with the load. Drift is the change over a run.

### Known limitations
- **Hardware encoders through FFmpeg are untested here.**
  - NVENC, Quick Sync and AMF all fail on this PC: the driver is too old for NVENC, and there is no Intel or AMD GPU.
  - Only the NVIDIA encoder through Media Foundation was tested.
  - Each is used only after its own test encode passes, so one that fails is skipped.
- **Changing encoder mid-recording saves a second file.**
- **Recovery.** A recording killed in its first second may have nothing to recover (the first fragment isn't written yet). Otherwise about the last second is lost.
- **Separate source tracks** are for editors: players play the first track only.
- **Default output device.** System audio still comes from the device that was the default when recording started; a device switched to during a recording isn't followed (Phase 11).
- **Recording stops (and is saved) when:**
  - the monitor's resolution changes below the recorded area;
  - the GPU is reset;
  - the disk is full.
- **OpenH264.** On Windows N editions without the Media Feature Pack, only OpenH264 is left, and it has no constant-quality mode: it gets a bit rate per pixel, so files are larger.
- **Bigger download.** The FFmpeg libraries are about 120 MB, against the 105 MB `ffmpeg.exe` they replace. Setup.exe is 130.8 MB (1.0.1: 118 MB).
- **Harmless FFmpeg log line.** FFmpeg logs "UDTA parsing failed retrying raw" (information level) when reading the track titles back; the titles read correctly.

## Phase 11 — Pro audio (2026-10-17)

Sound gets the controls a recording app needs. Recording follows the sound devices as they change, records one app's sound or leaves one out, and processes the microphone. Settings and the toolbar show levels, the microphone can be tried before recording, loudness is evened out when saving, and push-to-talk is available.

### Added
- **New project `CashGear.Capture.Audio`** (deferred from Phase 10). It takes over Recording's WASAPI code (the timestamped capture loop, the source factory, the device lists) and adds everything below that touches devices. The pure parts are in Core: the mixer and clock, and a new `Core.Audio` namespace with the DSP, the levels, the timestamp ledger and push-to-talk.
- **Devices are followed during a recording.**
  - **Following the default.** "Default output device" and "Default microphone" follow Windows' default (Sound settings, or the default unplugged): recording switches to the new device and says so in a notification.
  - **Waiting for a chosen device.** A chosen device that is unplugged is waited for and recorded again when it is back, also with a notification. A device that fails without any change (the audio service restarting) is tried again every second.
  - **The gap.** While no device is recorded, the track is silent. The first sound after a change lands where its timestamp says.
  - **Missing at the start.** A device not connected at the start no longer leaves its track out. The track waits for the device, and the notification says so.
  - **The saved notification** lists what happened and when ("At 0:30: Microphone (H5) was disconnected…").
  - **How.** `IMMNotificationClient` (through NAudio's notification client) is queued to a worker thread, where devices are opened and closed.
- **The output device can be chosen:** Settings › Recording › **System audio from**: the default output device (followed) or any output device. A chosen device that isn't connected stays chosen.
- **One app's sound, or all sound except one app** (Settings › Recording › **System audio**: All sound / Only one app / All sound except one app, with a list of running apps and **Refresh**).
  - **How.** WASAPI process loopback (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, include or exclude the process tree), so it works whichever device the app plays to. The app's sound is captured before the device's volume.
  - **Finding the app.** The app is chosen by program file (`chrome.exe`) and found at the top of its process tree (a browser plays from a child process). Where there are several trees, the one with sound open wins.
  - **Following it.** The app is looked for every second: one that starts later is recorded from then, and one that restarts is followed to its new process.
  - **Leaving out an app that isn't running** leaves out this app's own process instead, so everything is recorded until it starts.
  - **The list** shows programs with a window and programs with sound open (marked "playing sound"), named by their file description.
  - **The track** of one app's sound is named after the app.
- **Microphone processing** (Settings › Recording › Microphone processing), in this order, on 10 ms blocks on the capture thread:
  - **noise suppression** (RNNoise; below);
  - **noise gate**: −80 to −10 dB. It opens within 1 ms, closes 6 dB lower after a 150 ms hold, and fades 60 dB down;
  - **compressor**: −24 dB, 3:1, soft knee, 5/120 ms, with make-up that keeps a −12 dB peak where it was;
  - **limiter**: 2 ms look-ahead brick wall at −1 dBFS, on by default. Below the ceiling it changes nothing.

  Each block keeps the time its sound was captured: a `StampLedger` remembers the stamp of every packet, and the chain's delay is taken off. The delay is fixed when the recording starts (a disabled stage still delays), so turning a stage on or off while recording never moves the sound.
- **Noise suppression with RNNoise 0.2.**
  - **The DLL.** `tools\get-rnnoise.ps1` builds `rnnoise.dll` from the release tarball, which includes the model (SHA-256 pinned), with Visual Studio's C++ compiler. It is x64 with the static C runtime, uses SSE2, and picks SSE4.1 or AVX2 at run time. It ships as `rnnoise\` with its BSD licence. Without it the option isn't shown.
  - **How it runs.** Each channel is processed (once for a mono microphone), and the output is two RNNoise frames (20 ms) behind.
  - **Strength** (0–100%) mixes the original back in; 90% leaves noise about 20 dB down.
  - **Switched on**, it starts fresh and passes the original until its first real output.
  - **While recording**, the toolbar's Sound panel switches it, and the choice is kept for the next recording.
- **Level meters.** Each source has a peak bar, an RMS bar, a held peak and a clip mark. The mark lights red for 2 s when the sound reached full scale.
  - **On the toolbar:** a thin meter per source in the bar and a wider one in the Sound panel, 20 times a second.
  - **In Settings › Recording:** the microphone (as processed) and system audio, while the page is open, from the devices, app and processing chosen there.
  - **Measuring.** The mixer measures each track after volume and mute, and the mix before clamping, so a mix that clips shows it.
- **Microphone test** (Settings › Recording): **Record 5 seconds**, then **Play as recorded** or **Play with processing**. The processing runs again on each play, so changing it and playing again compares settings. It plays on the default output device.
- **Even out loudness to −16 LUFS** (Settings › Recording › Loudness, off by default), when a recording is saved or recovered.
  - **How.** Each sound track is measured with FFmpeg's `loudnorm` (EBU R128), then decoded, run through `loudnorm` with the measurements and encoded again to AAC; the video is copied. With the measurements loudnorm usually just turns the track up or down. It limits only where peaks would pass −1.5 dBTP.
  - **Silent tracks** (quieter than −60 LUFS) are copied.
  - **Recovery.** The setting is stored in the recording's manifest, so a recovered recording is saved the same way.
  - **avfilter.** This needs FFmpeg's `avfilter-12.dll`, which now ships in `ffmpeg\`. `get-ffmpeg.ps1` checks that the build has `loudnorm`, and `package.ps1` checks the six libraries.
- **Push-to-talk** (Settings › Hotkeys › Push to talk; no key by default). The microphone is silent except while the key is held, and 250 ms after.
  - The key is read every 10 ms rather than registered, so it still reaches the app in front, and a key on its own (F13, Scroll Lock) is fine. A key that is one of the hotkeys is refused.
  - The toolbar shows "Hold F13 to talk", and "Talking" in red while held.
  - It is separate from the toolbar's Mute.
- **Settings** (no schema change; new fields with defaults): `recording.systemAudioDevice`, `systemAudioScope`, `systemAudioApp`, `systemAudioAppName`, `microphoneProcessing` { `noiseSuppression`, `noiseSuppressionStrength`, `noiseGate`, `gateThresholdDb`, `compressor`, `limiter` }, `normalizeLoudness`, `pushToTalk`.

### Deviations from the spec
- **Per-app audio is offered from Windows 10 2004 (build 19041), not 20348.** Microsoft's documentation names 20348, but process loopback activates and records on this PC (19045), as OBS and NAudio rely on. Older builds don't get the option (and the app needs 19041 anyway). No Windows 11 machine was needed to test it.
- **RNNoise is built from source.** There is no official Windows binary, and a third party's would have no provenance to check (see LICENSES.md).
- **Strength is a mix of the original back in.** RNNoise has no strength setting of its own.
- **Loudness is evened out when saving, as an option.** There is no export yet (Phase 16). The loudness range target is 20 LU rather than loudnorm's default 7, so speech with pauses is turned up or down as a whole instead of compressed. The true-peak ceiling is −1.5 dBTP.
- **Each track is evened out by itself.** With a track per source, each source plays at −16 LUFS, so their sum in an editor is louder.
- **Push-to-talk reads the key instead of registering a hotkey**, so the key isn't taken from other apps, and keys on their own are allowed.
- **Settings' meters open the devices while the Recording page is shown**, so Windows' microphone indicator is on meanwhile.
- **A device missing at the start gives a silent track that waits for it.** In Phase 10 there was no track for it.
- **Only noise suppression is switchable on the toolbar**; the gate, compressor and limiter are set in Settings.

### Fixed during development
- **A loss never reported.** A device present at the start counted as already lost, so losing the default device with none to take over was never reported. Caught by the following-source tests.
- **Processing sent too early.** Settings sent processing changes to the meters while loading, before the page was shown. They are now sent only while the meters listen.
- **Settings compared unequal after loading.** The new nested `microphoneProcessing` was null in a default `RecordingSettings` but filled in after normalizing, so the two compared unequal. For example, the settings service raised Changed twice. It now defaults to its own defaults.
- **A false limiter failure.** The tests' decoder (`ffmpeg -ac 1`) mixes identical channels √2 louder, so a limited 0.891 peak read as 1.26. The test divides it out. (AAC also overshoots for about 5 ms where a full-scale tone starts out of silence.)

### Tested
- **738 automated tests** (729 pass, 9 skipped: the 6 RNNoise tests until `rnnoise.dll` is built, and 3 explicit). New:
  - **Core:**
    - the gate, compressor and limiter, including the ceiling never passed, the 2 ms delay exactly, and smooth gain before a spike;
    - the ledger, including stamps from each packet and splits at jumps and losses;
    - levels and meter ballistics;
    - the push-to-talk gate;
    - per-track and mix levels with clipping;
    - the new settings.
  - **Audio:**
    - following the default and chosen devices with a fake watcher (switch, loss reported once, a new default right after a loss, no default, unplug and replug, missing at the start, retry, dispose);
    - process trees and following an app (start, restart, leaving out, failure);
    - the chain's timestamps through the delay, jumps and live changes;
    - the monitor and the test recording;
    - RNNoise's delay, suppression, strength and switching on (skipped until built).
  - **Real devices:**
    - the device lists;
    - default system audio opened and followed;
    - a missing device waiting;
    - **process loopback of this process for 2 s with every frame there**;
    - an app that isn't running;
    - the app list;
    - the default microphone recorded for the test (0.5 s).
  - **Media:**
    - tracks at about −31 and −5 LUFS come out at −15.9 and −16.0 LUFS (ffmpeg.exe's `ebur128`), with true peak under −1 dBTP;
    - a peaky track (+15 dB would clip its beeps) at −16.0 with peaks limited;
    - the sound within 2 samples of its place, and the video frames identical;
    - a silent track copied;
    - loudnorm's report parsed.
  - **App:**
    - a 1.8× microphone tone limited to −1 dB at its own time, with the compressor turned on mid-way;
    - a recording saved evened out (−16 ± 1);
    - a recovered recording evened out;
    - a recording asked to leave out an app falling back to all sound;
    - the toolbar's meters, clip mark, push-to-talk text and noise-suppression switch;
    - Settings: devices, apps, processing, meters, microphone test, push-to-talk validation, and the window showing two meters on the Recording page only.
- **Manual on this PC** (Windows 10 22H2 19045, ZOOM H5 as output and microphone; see TESTING.md):
  - **One app only.** Two helper processes played 440 Hz and 1 kHz. Recording only the first heard 440 Hz at 0.196 and the other at 0.00006 (71 dB down); all but the first heard 1 kHz at 0.196 and 440 Hz at 0.00005.
  - **Packaging.** `ffmpeg\` holds the six libraries and both licence texts; Setup.exe is 143.7 MB.
- **Not run** (left for the user; see TESTING.md › Pending):
  - switching the default output device during a recording;
  - unplugging and replugging the H5 with the new following;
  - RNNoise on a real microphone (the DLL isn't built: no C++ compiler here);
  - push-to-talk with a real key;
  - loudness of a real recording.

### Known limitations
- **RNNoise isn't built on this PC.** Visual Studio has no C++ workload, so `rnnoise.dll`, its tests and the before/after acceptance (fan and keyboard noise) are pending. The 20 ms delay comes from reading RNNoise 0.2's source; a test measures it once the DLL exists.
- **One app = one process tree.** Two separate instances of the same program: only one is recorded. Sound an app has another process play (a system service or broker) isn't included.
- **Switching devices** leaves a few milliseconds of silence. When the default device goes and none takes over, the loss is reported after a second.
- **Evening out loudness** encodes the sound again (a second AAC generation) and adds measuring and encoding time when saving.
- **Push-to-talk** can't use a mouse button directly (map it to F13–F24), and the key also reaches the app in front.
- **The microphone light.** Settings' meters keep the microphone open while the Recording page is open.
- **Bigger download.** avfilter adds about 13 MB: Setup.exe is 143.7 MB (Phase 10: 130.8 MB).

## Phase 12 — Recording overlays + metadata track (2026-10-19)

Recordings can show what the user does: clicks, the pointer, keystrokes, a webcam and drawings on the screen. Every overlay is drawn on the GPU before encoding, or kept in a metadata file next to the video and added later. The metadata (the pointer on every frame, clicks, keys, focus changes, all on the recording's clock) is also what the editor (Phase 16) and auto-zoom (Phase 17) will work from.

### Added
- **New project `CashGear.Capture.Overlays`**: input hooks, keystroke privacy, pointer shapes, the Direct2D compositor and the webcam. The pure parts are in Core (`Core.Overlays`): the input timeline, the keystroke formatter, the overlay planner, the metadata journal and file, and the timed picture queue.
- **Input hooks** (12.1). `WH_MOUSE_LL` and `WH_KEYBOARD_LL`, plus WinEvents for the foreground window and the keyboard focus, on a thread of their own with a message loop.
  - **Minimal callbacks.** They only stamp the time (QPC) and queue the event; a "Recording input" thread does the rest.
  - **Only while recording.** The hooks exist only during a recording, and the keyboard hook only when keystrokes are on.
  - **Pauses.** Input made during a pause is left out (`RecordingClock.TryMap`); it isn't moved to the resume like a picture is.
- **Keystroke privacy.** A key typed into a password field, or into an excluded app, is never shown and never written anywhere.
  - **Password fields** are detected through UI Automation's `IsPassword` on the focused element (browsers report it for `<input type=password>`).
  - **Excluded apps** default to KeePass, KeePassXC, 1Password, Bitwarden and Windows' credential prompt (Settings › Overlays).
  - **How a key is decided.** Every focus change (a window coming to the front, the focus moving inside an app, a click) triggers a check on an MTA thread. A key waits for the checks made after the last change before it, and after any change within 40 ms after it. That covers a click into a password field followed at once by typing, whose focus event can arrive after the key.
  - **Fail closed.** An undecided key is hidden after 750 ms, and so is a key UI Automation can't vouch for (an elevated app, an error).
  - **The toolbar** can also hide keystrokes for a while.
- **Keystroke captions.**
  - **Shortcuts** read `Ctrl + Shift + S`; a repeat soon after counts (`Ctrl + Z ×3`).
  - **All keys** gathers typed text into one caption, using the foreground window's keyboard layout (`ToUnicodeEx` without disturbing its dead keys).
  - **AltGr.** Typing a character with AltGr isn't a shortcut.
  - **Display.** At most three captions show, the newest nearest the edge, fading after the last key. Position (6 anchors), font, size and fade time are settings.
- **Metadata track** (12.2): `name.cgmeta.json` next to each recording (Settings › Overlays, on by default).
  - **Contents:**
    - format version, parts with their start times, area, scale and fps;
    - whether the pointer and the overlays are in the video;
    - the keystroke mode;
    - the pointer shapes (premultiplied BGRA);
    - one row per frame (`[frame, x, y, shape, visible]`);
    - clicks (button, position, down and up times);
    - keystroke captions (only what the overlay shows);
    - foreground app changes (program and title; no title for an excluded app);
    - drawing on/off;
    - where the webcam was drawn.
  - **Clock.** All times are recording time (seconds), the clock pictures and sound use.
  - **Crash safety.** During a recording it is a JSON-lines journal in the temporary folder, flushed every second. Saving and recovering a recording turn it into the file.
- **Overlays drawn on the GPU** (12.3). `OverlayPlanner` decides what each frame shows. `OverlayCompositor` draws it with Direct2D on the recording's own D3D11 device: the captured picture is copied into a render target and the overlays drawn over it before read-back, so nothing goes through visible windows. The overlays:
  - **clicks:** an expanding, fading ring per press (colours per button), and a dot following a held button;
  - **pointer effects:** a halo or a spotlight (the rest dimmed);
  - **the pointer itself**, drawn by the app when it is bigger (1–3×) or hidden after standing still. Windows.Graphics.Capture then leaves its own out; a text I-beam is drawn black with a white edge;
  - **keystroke captions** in a dark rounded box (DirectWrite);
  - **the webcam.**
- **Click sounds** (optional): an 18 ms tick at each press, mixed into the recording's sound at the press's sample, as a source of its own. It is in the mix, or a track of its own when sources get tracks.
- **Adding overlays later.** Tray › **Add Overlays to a Recording…** picks a saved recording and writes `name (overlays).mp4`.
  - **How.** `VideoTranscoder` decodes the video, each picture gets the scene a live recording would have had at its time (same planner, same compositor), and it is encoded again with the recording settings' encoder; the sound and the webcam track are copied.
  - **The pointer.** One left out of the video is drawn here from the metadata's shapes.
  - **Window.** A progress window with Cancel.
- **Webcam** (12.4). Windows.Media.Capture: `MediaCapture` (exclusive, to choose the format nearest the settings, else shared) and a `MediaFrameReader` giving BGRA in real-time mode.
  - **Timing.** Each picture is stamped with its `SystemRelativeTime`, the QPC clock the screen and sound use, and placed by it, so it can't drift.
  - **Drawn** over the screen as a circle or rounded rectangle, with a border, mirrored if asked.
  - **Its own track.** It is also encoded as a constant-frame-rate track of its own, saved as a second video track ("Webcam", not the default) in the same MP4, from the part it was recorded during.
  - **Problems.** A missing camera, the camera privacy setting, or a camera that sends nothing leaves a recording without webcam, with a warning and a note. A camera another app is using is shared, at that app's format.
  - **Settings › Overlays › Webcam:** camera, picture size and rate, shape, size, border, mirror, and a preview of the recorded area to drag the webcam into place, showing the camera's picture while the page is open.
- **Drawing on the screen** (12.5). The **Draw on Screen** hotkey (`Ctrl+Shift+D`, registered only while recording) or the pen on the recording toolbar turns the pen on.
  - **The layer.** A transparent top-most layer over the recorded area takes the mouse: pen, highlighter, arrow, rectangle. It is in the recording: it isn't excluded from capture.
  - **The tools** (tool, colour, width, Undo, Clear, Done) are a row of the recording toolbar, which is excluded.
  - **Keys:** Esc ends drawing, Delete clears, Ctrl+Z undoes, 1–4 pick a tool.
  - **Afterwards.** With the pen off, the layer lets the mouse through and keeps the drawings; they can fade away after a set time (Settings).
  - **Clicks while drawing.** Clicks and keys made while drawing aren't drawn as overlays (the metadata notes the drawing).
- **Settings › Overlays** (new tab):
  - draw the overlays into the video, or keep it clean and add them later;
  - the metadata file;
  - clicks (colours, size, sound);
  - pointer (effect, colour, radius, dim, size, hide when idle);
  - keystrokes (mode, position, font, size, time, apps never shown);
  - webcam;
  - drawing (pen colour, width, fade).

  The Recording tab's click option moved here. Settings schema 4 moves `recording.showClicks` to `overlays.clicks.enabled`.
- **New dependency:** Vortice.Direct2D1 3.8.3 (MIT; Direct2D and DirectWrite bindings).

### Deviations from the spec
- **The webcam's own video is a second track of the MP4**, not of an MKV: recordings are written as fragmented MP4 and saved as MP4 since Phase 10. It is encoded into a file of its own while recording, so an encoder switch of the screen never touches it, and merged when saving. After an encoder switch, a later part's webcam track starts at its first keyframe in that part.
- **No background blur.** Windows Studio Effects doesn't exist on Windows 10, and the spec allows skipping it in v1.
- **Webcam pictures show from the first frame at or after their capture time.** The screen's pictures land on the nearest frame, so the webcam can be up to one frame later than the screen (measured: 5 ms after the beep it was stamped with).
- **With keystrokes on, frames are made 100 ms after their time** (50 ms otherwise), so a key's privacy check is done before its frame is drawn.
- **The drawing hotkey is registered only while recording**, so `Ctrl+Shift+D` isn't taken from other apps the rest of the time. Settings shows it as "Works while recording".
- **The drawing tools are a row of the recording toolbar**, not a window of their own: the toolbar is already left out of the recording and stays above the drawing layer.
- **Click sounds are mixed into the recording, not played aloud**, and only while recording: adding overlays later copies the sound as it was.
- **With "add later" and a bigger or hiding pointer, the pointer is left out of the video** so it can be drawn later; such a recording has no pointer until its overlays are added.
- **Keystrokes stay in the metadata file** next to the video (only what the overlay shows). That is the "overlay events" the spec allows; turning the keystroke overlay off keeps none.

### Fixed during development
- **A frozen recording.** Taking the read-back lock in the capture callback froze the video thread the first time Direct2D drew. Windows may hold the device's lock while it calls back. The callback's copy relies on the device's multithread protection again.
- **A lost last picture.** The transcoder's re-encoded pictures had no duration, so the MP4's edit list left the last one out. Each now lasts one frame, and the webcam merge does the same.
- **The mouse hook's injected flag** is `LLMHF_INJECTED` (0x1), not the keyboard's 0x10.
- **Layout-dependent tests.** The keyboard layout here switches to Arabic: tests compare captions with what the typed key produced instead of assuming a US layout.
- **Desktop tests of different assemblies collided.** The test assemblies run side by side. One assembly's top-most test window covered another's (the WGC window test failed that way in most full runs, beyond its known flakiness), and synthetic clicks landed in the wrong test.
  - The desktop collection now holds a named semaphore, so the assemblies' desktop tests take turns.
  - Tests that send input hold another.

### Tested
- **813 automated tests** (802 pass, 11 skipped):
  - the 6 RNNoise tests until `rnnoise.dll` is built;
  - 4 explicit (now also the browser password test);
  - the real-microphone test, as Windows reports no microphone right now.

  Phase 11 had 738; the four CPU click-highlighter tests went with it. New:
  - **Core:**
    - `TryMap` across pauses;
    - the input timeline (pointer, clicks, captions, drawing, pruning);
    - the keystroke formatter (combos, repeats, typing, AltGr, modifiers alone, Off);
    - the metadata journal (round trip, a crash-cut last line, the timeline rebuilt) and file;
    - the overlay planner (ring timing, held dot, halo and spotlight, the app's pointer and its idle fade, captions, the webcam's place);
    - click sounds;
    - the timed picture queue and 30 simulated minutes of a wandering camera;
    - the overlay settings and the schema 4 migration.
  - **Overlays:**
    - hooks with SendInput into a test window;
    - UI Automation reading a WPF PasswordBox and TextBox;
    - the privacy timing;
    - keys typed into a password box never kept;
    - pointer shapes (arrow and I-beam);
    - the compositor's pixels on a real D3D11 device (8 tests);
    - the real camera list;
    - the explicit browser test.
  - **Media:**
    - the transcoder (every picture kept and changed, the sound copied exactly);
    - the webcam merge (3 tests).
  - **App:**
    - live sync of a click and Ctrl+S;
    - added-later sync;
    - click sound and the app's 2× pointer;
    - the metadata of a recording and of a recovered one;
    - the webcam with a fake camera against a tone, and a camera that sends nothing;
    - recovery keeping the webcam track;
    - drawing with the mouse into a recording;
    - the drawing shapes and model;
    - the recording-only hotkey;
    - Settings › Overlays (view model, window, webcam preview);
    - the toolbar's keystroke switch;
    - Add Overlays with a fake renderer;
    - the app's whole service container building.
- **Measured on this PC** (see TESTING.md):
  - the ring and the caption on exactly the frame of the click's and the key's hook time, live (4 of 4 runs) and added later; the window's own reaction 0–1 frame later;
  - the click tick 0.1 ms from the click;
  - the webcam's track and overlay 5 ms after the beep they were stamped with;
  - a mouse-drawn stroke in the recording;
  - acceptance in a real browser (the explicit test, run with the user's go-ahead): "secret" and Ctrl+A typed into a password field in Microsoft Edge showed no keys, 3 of 3 runs (7 hidden, 0 shown; the check found a "password field" in `msedge.exe`).
- **Not run** (pending in TESTING.md):
  - a real webcam, and the 30-minute webcam/sound sync with one (no camera here);
  - a login on a real site by hand;
  - the drawing hotkey and the click sound by hand.

### Known limitations
- **No camera on this PC.** The webcam is tested with a fake camera; a real camera and the 30-minute sync run are pending (TESTING.md).
- **UI Automation decides privacy.** An app that doesn't report its password fields to UI Automation isn't recognized; exclude it in Settings. Keys are hidden whenever the check fails, so an app UI Automation can't read (elevated, hung) shows no keys.
- **Dead keys.** In "All keys", a dead-key composition (é from ´ then e) shows the base letter.
- **The app's own pointer** is drawn from the shape Windows gives (scaled from its size at the primary monitor's scale); an inverting I-beam becomes black with a white edge, and animated pointers show their first frame.
- **The drawing layer** covers the recorded area only, and a full-screen app may cover it; while the pen is on, the apps beneath don't get the mouse.
- **Adding overlays later encodes the video again** (a second generation) and takes about as long as the recording on the software encoder.
- **Cursor shapes and positions are per frame**, not at the hooks' full rate.

## Phase 13 — Capture parity with FastStone (2026-10-21)

FastStone Capture's ways of capturing, in CashGear Capture: a floating capture toolbar, six more capture modes (three of them transparent outside their shape), Repeat Last Capture, a delay per kind of capture, and four screen tools.

### Added
- **Floating capture toolbar.** Tray › **Capture Toolbar**, a hotkey or `--toolbar` (Settings › Capture can show it at every start; off by default).
  - **Buttons:**
    - every capture mode (region, window, active window, full screen, scrolling, fixed size, freehand, polygon, rounded, object, open menu, text);
    - repeat, record, the screen tools;
    - menus for the delay and for what happens after a capture (editor, file, clipboard);
    - Settings, Hide.

    Line icons are drawn in the theme's text colour; tooltips name the hotkeys. A right-click on the fixed-size button offers the sizes.
  - **Never in the way.** It is always on top and never takes the focus, so Active Window still means the user's window and an open menu stays open. It is left out of every capture and recording, and stays put while a capture hides the app's other windows.
  - **Docking.** Drag it by its grip. Dropped within 24 px of an edge of a monitor's work area it docks flush against it: a column on the left and right edges, a row at the top and bottom.
  - **Placement.** Positions are physical pixels, so it lands exactly at any monitor scale. Where it was is remembered (on the primary monitor if its monitor is gone).
- **New capture modes** (tray › **More Captures**, the toolbar, hotkeys with no default, and `--capture-fixed`, `--capture-freehand`, `--capture-polygon`, `--capture-rounded`, `--capture-object`, `--capture-menu`):
  - **Fixed-size region.**
    - A frame of a set size follows the pointer by its top-left corner (so the loupe shows that corner's pixel).
    - Click to place it, drag to move it, arrows nudge it (Ctrl: 10 px).
    - Tab and Shift+Tab switch between nine presets (320 × 240 to 1920 × 1080) and the size typed in Settings › Capture.
    - Enter or a double-click captures. The size used last is kept.
  - **Freehand region.** Draw with the button held; letting go captures. The trail is closed straight back to its start and simplified (within 0.75 px).
  - **Polygon region.** Click the corners. A click on the first corner (a ring shows when the pointer is on it), a double-click or Enter captures; Backspace takes the last corner back.
  - **Rounded region.** Like a region (or a clicked window), with rounded corners. The mouse wheel sets the radius (shown in the label), which is kept.
  - **Object.** The part of a window under the pointer is lit — a button, a text box, a toolbar, a panel — with its kind and name ("button "OK"").
    - **The wheel** goes out to what contains it and back in; a click captures exactly its rectangle.
    - **How it is found.** The window under the pointer (from the snapshot taken before the overlay) is lit at once. UI Automation then finds the parts inside it on a thread of its own; only the newest request waits, so a fast pointer costs one lookup per answer.
  - **Open menu.** Start it, then open the menu (or context menu, with submenus) during the countdown: 3 s by default, its own delay in Settings. The menus are captured on their own, without the rest of the screen, transparent between a menu and its submenu.
    - **Classic menus** are found in any app.
    - **Apps that draw their own** (browsers, Office, WPF): the title-less popups of the app in front, above its window.
    - **Not menus:** shadows, click-through effects and full-screen glass.
  - **Transparency.** Freehand, polygon, rounded and menu captures are transparent outside their shape, with soft (anti-aliased) edges. They are always saved as PNG, and are white where transparent for apps that ignore alpha.
- **Repeat Last Capture** (`Ctrl+Alt+Shift+PrintScreen`, tray, toolbar, `--repeat-last`). Takes the last capture again with no selection, after the delay of its kind:
  - **An area** (any region shape or an object): exactly the same pixels, live, with the same shape;
  - **A window**, the active window or a scrolling window: the same window while it is still that window (the same handle, class and process; the title may change), else a message;
  - **full screen, text or the open menus:** those again.

  It is remembered across restarts in a new `state.json` in the data folder. That file is kept apart from settings.json and also holds the toolbar's and ruler's places, picked colours and the magnifier's zoom.
- **A delay per kind of capture** (Settings › Capture › Delay per kind of capture). Each kind can have its own delay or use the general capture delay, which the tray's Capture Delay menu and the toolbar still set. The countdown names what it is about to capture.
- **Screen tools** (tray › **Tools**, the toolbar, hotkeys with no default, `--magnifier`, `--color-picker`, `--ruler`, `--crosshair`):
  - **Magnifier.** A live lens centred on the pointer, 2× to 8×.
    - **Controls.** The mouse wheel or + and − zoom; arrows move the pointer a pixel (Ctrl: 10); a click or Esc closes it.
    - **What it shows:** the centre pixel framed, with its position and colour. The zoom is kept.
    - **How it reads the screen.** Every frame it reads the square under the pointer with GDI, shown pixel for pixel on the monitor's scale.
    - **The mouse.** While it is open a low-level mouse hook takes the wheel and the buttons before any window gets them; the pointer moves freely.
  - **Colour picker.** Click any pixel on any monitor. The screen is frozen meanwhile, without the pointer and undimmed, with the loupe.
    - **Formats.** The colour is shown as HEX, RGB, HSL and C# `Color.FromArgb(255, r, g, b)`, each with a Copy button.
    - **Copied at once** in the chosen format (HEX by default; also Settings › General).
    - **History.** The last 16 colours are kept, newest first.
  - **Screen ruler.** Marks in pixels, inches or centimetres (right-click), drawn in physical pixels. Inches and centimetres use the monitor's DPI (96 per inch at 100%), or the pixels per inch set in Settings › General to measure on the glass.
    - **Mouse:** drag to move, drag the far end to change the length, double-click to turn it.
    - **Keys:** arrows nudge (Shift: the length), Esc closes.
    - **The pointer.** A red line follows the pointer along it with the distance from its zero end.
    - **Remembered:** its place, length, direction and unit.
  - **Crosshair.** A line across and down every monitor through the pointer, and a readout of its position on the screen and on its monitor, with the monitor's scale. It follows the pointer every frame and lets clicks through to the apps underneath.
- **Settings.**
  - **Capture:** the toolbar at startup; delays per kind; the fixed size (any size can be typed) and the corner radius.
  - **General › Screen tools:** the ruler's pixels per inch and the colour copy format.
  - **Hotkeys:** 13 new rows, and the tab scrolls.
- **File names and history.** `{kind}` gives `object` and `menu`, and the history says Object and Menu.
- **Inside.**
  - **Core:**
    - every overlay mode is a selection model behind one interface (`ISelectionModel`, `SelectionView`): region, fixed size, shape, object, point;
    - `CaptureShape` and `ShapeMask` (an anti-aliased rasterizer: nonzero winding, four sub-rows, exact across);
    - the toolbar's docking; the open-menu rules; colour formats; the ruler's marks; the magnifier's sampling.
  - **Interop:** UI Automation is read further through the source-generated COM interfaces: `IUIAutomation` to `CreateTrueCondition`, `IUIAutomationElement` to `get_CachedBoundingRectangle`, the cache request and the element array.

### Deviations from the spec
- **The magnifier reads the screen with GDI** — neither Windows.Graphics.Capture nor the Magnification API. WGC shows its yellow border on Windows 10, and reading a small square with GDI each frame is cheap. Its lens is an opaque window left out of captures, so it never sees itself.
- **The magnifier takes the mouse with a low-level hook**, not a window over the screen. While a see-through window covers the screen, Windows composes some pixels under it a level off, even in captures that leave the window out. That made the lens's colours off by one.
- **Object capture walks UI Automation down from the window under the pointer** instead of using its hit test, which would find the overlay that covers the screen.
  - **Whole controls.** It stops at controls whose insides aren't parts of their own (a button's label, the text inside an edit box).
  - **The window** is its visible frame, not UI Automation's rectangle with the invisible resize borders.
- **Menu capture is automatic** after its delay: a selection would close the menu at the first click. Menus that are part of a window (UWP/WinUI flyouts) can't be told apart from the window.
- **Shapes are always saved as PNG**, whatever the format setting, since only PNG keeps their transparency.
- **The fixed-size frame follows the pointer by its top-left corner**, not its centre, for pixel-exact placement with the loupe.
- **No auto-hide** when the toolbar is docked. A column (or row) too long for the monitor wraps into a second one.
- **The ruler and the crosshair's readout are opaque**, for the same reason as the magnifier: a see-through window would put pixels under it a level off in captures.

### Fixed during development
- **The toolbar.**
  - **The drag lagged the pointer.** It took its origin from the live pointer when the press was handled. The pointer had moved on by then, and the last move before the release could be missed.
  - **A docked column came back too wide.** Created narrow, it started at Windows' minimum window width (136 px) until fitted to its buttons again once shown.
- **WPF and tool windows** (each found with a probe):
  - **Capture exclusion.** Windows won't leave WPF's `AllowsTransparency` (per-pixel alpha) windows out of captures, so every tool window is an opaque WPF window.
  - **Click-through.** WPF takes `WS_EX_LAYERED` out of every extended-style change of a window that isn't `AllowsTransparency`, so the bit could not even be set, and clicks didn't go through the crosshair. The window procedure is subclassed to put it back after WPF has handled `WM_STYLECHANGING`.
  - **Thin lines.** WPF windows are overlapped windows, which Windows keeps at least 2 px tall; the borderless tool windows are made popups, so a crosshair line is 1 px.
- **The wheel** only reaches a window that appeared under a still pointer once the pointer moves — the reason the magnifier uses a mouse hook.
- **Object capture.** UI Automation's raw view puts a button's label inside the button, so the label was lit instead of the button. WPF panels have no element at all, so a WPF button goes straight out to the window.
- **Three tests of the hotkey registrar** still counted the six old default hotkeys after Repeat got its default.
- **Found by the mixed-DPI run** (the primary monitor at 150%):
  - **The toolbar didn't fit.** A column of its 20 buttons is taller than a 1080p work area at 150%. It now wraps into a second column (or row) when one would be longer than the monitor's work area.
  - **The toolbar came back in the wrong place.** Restored onto a monitor of another scale, it was measured at the old scale and ended at the top of the work area. It is now moved onto its monitor first, then measured and placed.
  - **The crosshair's lines were rescaled.** When most of a line moved onto the other monitor, WPF scaled the window for that monitor's DPI (a 1-px line became 2 px wide and 1.5 times as long). The lines are put back after every DPI change.
  - **Fixed: the pointer's I-beam was invisible (Phase 12, fixed after the phase).** Scaled up for a 150% primary, Windows' black-and-white I-beam becomes a 48 × 48 colour pointer without alpha: its mask lets the whole screen through and its white beam inverts it. `CursorShapes` read such a pointer as fully transparent, so a recording that draws the pointer itself (bigger, or hidden when idle) showed no I-beam. Colour pointers without alpha are now drawn like black-and-white ones, the inverting parts black with a white edge. The arrow (48 × 48 with alpha) was never affected. A new test builds the same inverting I-beam both ways and needs no display change.

### Tested
- **941 automated tests** at 100% + 100% (930 pass, 11 skipped: the 6 RNNoise tests until `rnnoise.dll` is built, 4 explicit, and the real-microphone test, as Windows reported no microphone then). Phase 12 had 813. New:
  - **Core:**
    - the shape masks (coverage, rounded corners, a figure-eight lasso, menu rectangles, existing transparency, negative origins);
    - every selection model (fixed size, freehand, polygon, rounded, object, point) and the factory;
    - per-mode delays, the new hotkeys, an old settings file, `state.json` (round trip, bad files);
    - the toolbar's docking, the open-menu rules, colour formats (CSS HSL), the ruler's marks at 30 to 300 dpi, the lens maths;
    - every command-line switch.
  - **App, on the desktop, on every monitor in turn** (SendInput over the tests' own windows):
    - region, fixed size, freehand, polygon and rounded captures end to end, each checked pixel by pixel against the screen;
    - **Repeat Last Capture, acceptance:** the identical pixel rectangle and pixels for each, after the same delay;
    - a window capture repeated while open and refused once closed;
    - object capture (the button's rectangle exactly, the wheel out to the window's frame) and UI Automation through the generated COM interfaces, in this process and across processes (the taskbar's Start button);
    - a Win32 menu and a WPF context menu captured exactly, and the message with none open;
    - the toolbar (placement, capture exclusion, no focus taken, a SendInput drag docking it flush on every monitor, remembered);
    - the magnifier (exactly the screen under the pointer, the wheel through its hook, a click closing it), the colour picker (a SendInput pick), the ruler, the crosshair (a click through it, a capture across its lines unchanged);
    - the Settings view model.
- **Mixed DPI and negative coordinates** (the TESTING.md matrix), with the displays changed by hand:
  - **The primary (1920 × 1080) at 150% and the other monitor at 100% to its right:** every Phase 13 test passes (after the fixes above).
  - **The same with the other monitor left of the primary at (−1280, 200):** every Phase 13 test passes.
  - **The whole suite in those layouts** (941 tests): at 150%, one failure, a Phase 12 test (fixed afterwards: the I-beam above). Left of the primary, also the crosshair test, whose button window the test placed in DIPs, off the screen at negative X; placed in physical pixels, it passes.
  - **Back at 100% + 100%:** the Phase 13 desktop tests pass again.
- **Not run** (pending in TESTING.md):
  - each mode, the toolbar and the tools by hand in the installed app;
  - object and menu capture in other apps (a browser, Office, Explorer);
  - the magnifier and the crosshair looked at by eye (they are left out of every capture, so no test can see them).

### Known limitations
- **Object capture sees what UI Automation sees.** Apps that don't report their parts (some games, custom-drawn apps) and apps running as administrator offer only the whole window. A deep page in a browser can take a moment; the window is lit meanwhile.
- **Menus inside a window** (UWP/WinUI flyouts) can't be told apart from the window, and a popup that isn't a menu (a tooltip) of the app in front is taken with the menus.
- **While the magnifier is open** clicks go to no app, and Windows skips its mouse hook if the app is busy for more than about a third of a second.
- **The ruler and the crosshair's readout are opaque.**
- **A repeated scrolling capture** scrolls from where the window is now, like the first.
- **HDR monitors** are not handled yet (Phase 18): the colour picker and the magnifier read SDR values.

## Phase 14 — Output Destinations (2026-10-21)

What happens after a capture is now a workflow: an ordered list of destinations — the editor, a file, the clipboard, a printer, Word, PowerPoint, FTP/FTPS and SFTP servers, HTTP uploads (with the link copied) and other apps. Each destination has a Test button, and passwords and tokens are kept encrypted with DPAPI.

### Added
- **The after-capture workflow** (Settings › Output). Its steps run in order, and each can be switched off.
  - **Editing it:** a step can be moved up or down, removed, or added from every destination set up so far, including ones added on the Destinations tab a moment ago.
  - **Steps after the editor wait for it.** The editor shows a **Continue** button that runs them on the edited image, for example "Continue: Save to file → Office → Copy the link".
  - **Failures.** A step that fails doesn't stop the others. One notification sums up the run: what was saved, uploaded and copied, and what failed. A click shows the saved file, or else opens the link.
  - **Old settings.** The three after-capture switches became the first three steps, in the order they always ran (save, copy, then the editor), so an upgraded copy does exactly what it did. New installs open the editor, as before.
  - **Nothing to do.** With every step off, captures are refused with the same warning as before.
- **What happens to a recording** (Settings › Output). It is a workflow of its own, run after the MP4 is saved.
  - **The clipboard** takes the recording as a file, to paste into a folder, a chat or an email.
  - **Also:** servers, uploads, apps and Copy the link.
  - **Off by default:** a recording is only saved, as before.
- **The editor's Send to menu** sends the image as it is now, annotations included, to any destination. Its file is sent as it is when it has no unsaved changes. Save to file makes a new file in the capture folder, which Ctrl+S then updates.
- **The toolbar's after-capture menu** lists the workflow's steps, each switched on or off there, and "Change the workflow…" opens Settings.
- **Destinations** (a new Settings › **Destinations** tab):
  - **Printer.**
    - **Where it prints:** through the print dialog (printer, copies, orientation), or straight to the chosen printer or Windows' default.
    - **On the page:** one capture per page, as large as fits, filling the page (the edges cut off), or as big as it was on the screen. The page is portrait or landscape, or landscape only for wide captures, with margins of 0 to 50 mm.
  - **Word and PowerPoint** (Open XML SDK; no Office needed).
    - **The file:** a new `.docx` or `.pptx` in the capture folder, named like the capture, or one added to at the end (a paragraph, or a slide on the blank layout).
    - **The picture's size:** as big as it was on the screen (its pixels at the monitor's scale), shrunk to fit between the margins or on the slide. A new presentation is 16:9.
  - **FTP, FTPS and SFTP servers** (FluentFTP, SSH.NET).
    - **Settings:** host, port, user, password, a folder (made if missing; empty is the login folder), and passive or active mode for FTP.
    - **A link:** a public address with `{file}` gives each upload a link.
    - **SFTP** can log in with a key file and its passphrase.
    - **No overwriting:** a name that is taken gets " (2)".
    - **Who the server is:** an FTPS certificate that Windows doesn't trust (often self-signed) is accepted only by the SHA-256 fingerprint the user chose to trust after a Test. An SFTP server's host key is always trusted that way, on first use as OpenSSH does, and a key that changes later is refused. Nothing is sent, not even the password, before the server is trusted.
  - **HTTP uploads.**
    - **The request:** a multipart/form-data POST with the file field, any form fields and headers, and one secret header (`Authorization: Bearer …`, `Client-ID …`), whose value is kept with DPAPI.
    - **The link** is found in the JSON reply by a dot path (`data.link`, `files[0].url`; a leading `$.` is allowed). With no path, the whole reply is the link. Any self-hosted API or image host fits.
  - **Open with** a program (Paint.NET, Photoshop, …) and a command line with `{file}` and `{folder}`. A capture that wasn't saved is copied to `outgoing\opened` first and kept for a day, so the program can still read it after the workflow.
  - **Copy the link:** the links the steps before it made, one per line.
  - **Test buttons** check the set-up as it is in the window, typed passwords included. They never print, open Office or leave a file behind:
    - **printer:** it is installed, and how pages will be laid out;
    - **Word, PowerPoint:** the file is a document of that kind and isn't open in Office (or the folder takes new files);
    - **servers:** log in, make the folder, write and delete a test file;
    - **uploads:** post a 1 × 1 PNG and show the link;
    - **apps:** the program is there (on the PATH too); nothing is started.

    A certificate or key that isn't trusted yet is shown with its fingerprint and a **Trust it** button, which tests again.
- **Secrets.** Passwords, key passphrases and header values are encrypted with DPAPI for the current Windows user, in `%LocalAppData%\CashGear\Capture\secrets.json` (never settings.json).
  - **Bound to their key:** a value copied to another key can't be read.
  - **In the window:** they are typed into password boxes and never shown again. They are written only on OK or Apply, and removed with their server or upload. An emptied box keeps the saved value; **Forget** removes it.
- **Inside.**
  - **Outputs** (new project):
    - `IOutputDestination` and a built-in registry (no plugins); `OutputItem`;
    - the workflow runner;
    - the destinations above;
    - `DpapiCredentialStore` and `PendingCredentials`;
    - `LinkPath`.
  - **Core:** `DestinationSettings` (schema 5), `PrintLayout`, and copying files to the clipboard (`IOutputService`).
  - **App:**
    - the editor, file, clipboard and printer destinations;
    - `OutputWorkflow` (captures, recordings, the editor's Continue and Send to, the notification);
    - the Settings view models.
  - **Temporary files.** A capture that needs a file before it is saved gets a temporary one in the format of Settings › Output, in `%LocalAppData%\CashGear\Capture\outgoing`. It is deleted when the workflow ends, and what a crash leaves is cleaned after a day.

### Deviations from the spec
- **FTP and SFTP are tested against servers inside the test project**, not a Docker container: this PC has neither Docker nor CI.
  - **FTP:** a small FTP/FTPS server written for the tests (passive and active, explicit and implicit TLS with a throwaway certificate).
  - **SFTP:** an SSH server on FxSsh 1.4.0 (MIT, test-only) with a small SFTP v3 handler, since FxSsh's own SFTP code is only in its sample app.

  Both listen on 127.0.0.1 only.
- **`IOutputDestination` also has `TestAsync`** (the Test button). Its results can carry a link, a file and an untrusted fingerprint.
- **Test buttons where there is something to set up:** the printer, Word, PowerPoint, and every server, upload and app. The editor, Save to file, the clipboard and Copy the link have none.
- **Secrets are kept apart from settings.json**, in `secrets.json` in LocalAppData. The settings file stays readable and editable by hand, and secrets don't roam with the profile.
- **The link's path is a dot path**, not full JSONPath.
- **Recordings have their own workflow** (the spec only has one, for captures); by default it does nothing.
- **The printer's Test prints nothing**: it names the printer and the layout.

### Fixed during development
- **Encrypted key files.** SSH.NET reports one opened without its passphrase as a BouncyCastle "pad block corrupted" error, not its own exception. Such keys are now recognised (PEM's ENCRYPTED headers, OpenSSH's bcrypt) and the passphrase is asked for.
- **Fingerprints written another way** didn't match, such as `sha256 ab cd …` against `SHA256:AB:CD:…`. Hex fingerprints are now compared without their prefix, colons, spaces and case.
- **`{counter}`.** Naming each capture as it entered the workflow would have used up a counter number for captures that were only copied or edited. The name is now made the first time a step needs a file.
- **Settings didn't check a destination again** when one of its fields changed (only when one was added or renamed).
- **An emptied password box** would have deleted the saved password. It now keeps it, and Forget removes it.
- **Explicit styles on inputs** in the new Settings XAML would have replaced the Fluent theme's styles, so they were changed to plain margins.
- **The toolbar's after-capture button** crashed when its window had no view model of its own, as in the toolbar's desktop tests. That took the whole App test process down; the desktop run found it.

### Tested
- **1117 automated tests** at 100% + 100%: 1107 pass and 10 are skipped (the 6 RNNoise tests until `rnnoise.dll` is built, and 4 explicit). The real-microphone test ran this time. Phase 13 had 941. New:
  - **Core:**
    - the schema 5 migration, for every combination of the old switches;
    - the Destinations section: defaults, cleaning, unique ids, equality, and the JSON round trip without secrets;
    - the page layout: fit, fill, actual size from 100% to 200%, orientation, margins.
  - **Outputs** (a new project, 96 tests):
    - **FTP, FTPS and SFTP** against servers the tests start on 127.0.0.1:
      - passive and active FTP, explicit and implicit TLS;
      - untrusted and changed certificates and host keys, refused before the password is sent;
      - logins with passwords, RSA keys and encrypted keys.
    - **HTTP uploads** to Kestrel: the multipart form, the headers, the secret header, the link by path, and the errors.
    - **Word and PowerPoint** files that pass the Open XML validator: new, added to twice, open elsewhere, not a document.
    - **The rest:** DPAPI secrets, Open with, the workflow runner.
  - **App:**
    - **The workflow end to end,** saving files for real: the old switches behave as before; the notifications; `{counter}`; temporary files; recordings; the editor's Send to and Continue.
    - **The printed page,** rendered to pixels and written to XPS.
    - **Settings:** the workflow lists and the Destinations tab, and the window's XAML on the desktop.
- **The desktop run found a crash:** the toolbar's after-capture button crashed the App test process (Fixed during development). After the fix, the App tests pass: 238, with 1 explicit skip.
- **Not run** (pending in TESTING.md):
  - a real printer;
  - the files opened in Word, PowerPoint and LibreOffice;
  - real FTPS, SFTP and upload servers;
  - Open with a real app;
  - the workflow by hand in the installed app.

### Known limitations
- **The workflow runs within the capture.** A slow upload holds the next capture until it is done, or until its 2-minute timeout. There is no progress or Cancel for uploads (Phase 18).
- **Office.** Only the picture goes in (no caption). A document open in Word or PowerPoint is refused until it is closed. A new Word document is A4, or US Letter where the region doesn't use the metric system.
- **The printer.** There is no print preview. With 0 mm margins, the printer's own unprintable edge cuts the picture off.
- **FTP.** No FTP proxies. FTPS servers that require the data connection to reuse the control connection's TLS session (vsftpd's `require_ssl_reuse`) aren't tested.
- **SFTP.** Keys in the formats SSH.NET reads (OpenSSH, PEM, PKCS#8). No SSH agent or Pageant, and no keyboard-interactive login.
- **HTTP.** POST with multipart/form-data only, and one secret header. The link is found by path, not built from pieces of the reply.
- **A notification's click** shows the first file, or opens the first link.

## Phase 15 — Image Editor Extras, Batch, PDF (2026-10-21)

The editor gets effects, a watermark, a spotlight and a magnifier, and more ways to change the whole image. Effects are shown on the image while their window is open, and Apply keeps exactly what was shown. Text annotations and watermarks can be written in Arabic. Three new tools work on many pictures at once: Batch Process, Combine Images and Images to PDF. A capture can also become a PDF as a step of the workflow.

### Added
- **Effects** (the editor's new **Effects** menu), on the whole image or on the part selected with the new **Area** tool:
  - **Edges and frames:**
    - **Drop shadow:** softness, offset, darkness and colour. It follows the picture's shape.
    - **Torn edge:** each side on or off, how deep, how far apart the teeth. **New pattern** tears it another way, and the same pattern comes back every time until then.
    - **Faded edges, rounded corners.**
    - **Border:** it follows rounded corners and torn edges.
    - **Perspective:** turned left/right or up/down, up to 60°.
  - **Adjustments:** brightness and contrast, grayscale (at once, no window), sharpen.
  - **Watermark:** text in any language, or a picture file (scaled).
    - **Where:** a 3 × 3 grid of places at a distance from the edges, or repeated over the whole image with a gap between.
    - **How:** opacity, angle, and a light shadow so it reads on any background.
- **The Effects window.**
  - **The list:** effects applied from top to bottom; add, remove, reorder. Below it, the selected effect's settings.
  - **Preview:** the image shows the result while the window is open. It is worked out on another thread a moment after each change, so sliders stay smooth.
  - **Apply** keeps exactly the previewed pixels, as one undo step named after the effect or the preset.
  - **Annotations:** a shadow or a border keeps them editable (moved with the image). Other effects merge them into the image first, and Undo brings them back.
  - **A selected area** is lifted: what the effects cut away shows a colour of your choice (white, any colour or transparent), and a shadow or border reaches past the area.
  - **Errors** (a watermark picture that's gone) are shown in the window, and Apply waits until they're fixed.
- **Presets** in `presets.json` next to settings.json:
  - **Effect presets:** a list of effects saved by name (Save as…, Delete), applied in one click from Effects › **Presets**, or chosen in Batch Process. Four come with the app (shadow; torn bottom, rounded or thin border with a shadow).
  - **Watermark presets**, saved from the watermark's settings. One comes with the app ("Confidential, across the image").
  - **The image tools' last choices** are kept there too.
- **New tools:**
  - **Area (`M`):** the part of the image that effects change.
  - **Spotlight (`O`):** dims everything outside its rectangle (colour, and how dark as Dim). All spotlights share one dimmed layer, so several can be lit, over blur and pixelate and under the other annotations.
  - **Magnify (`G`):** drag over a part, and a round callout shows it 2× to 8× larger, placed beside it where it fits, joined by a line.
    - **Moving:** drag the callout or the source circle to move that one alone.
    - **Grips:** they set the source's size and the zoom; the panel calls Strength "Zoom".
- **The Image menu:**
  - **Rotate by an angle** (previewed). Quarter turns stay exact and keep annotations; other angles grow the canvas, with transparent or coloured corners.
  - **Flip** horizontally or vertically. Text stays readable.
  - **Canvas size** (previewed): where the image goes on a 3 × 3 grid, and the colour around it.
  - **Trim plain borders:** cuts away rows and columns of the corner's colour, and says when there are none.
- **Arabic and other right-to-left text** in text annotations and watermarks.
  - **Shaping:** letters are joined into their contextual forms (HarfBuzz), with kerning.
  - **Order:** mixed Arabic, English and numbers come out in display order (the Unicode bidirectional algorithm from Windows' own ICU).
  - **Alignment:** text starting with a right-to-left letter is right-aligned, and the in-place text box types right to left.
  - **Fonts:** characters a font lacks come from one that has them.
- **Batch Process** (tray › Tools, and the toolbar's tools button):
  - **Steps:** a size (percentage, fit in a box, width, height; never larger if ticked), an effects preset, a watermark preset, a format (each file's own, PNG, JPEG with its quality, BMP).
  - **Names:** a template with `{name}`, `{counter}`, the file's date and time, `{width}` and `{height}`, with an example of the first name.
  - **The output folder:** a name already taken gets a number, is replaced, or that picture is skipped.
  - **Running:** a few pictures at a time on other threads (half the processors, at most 4), each read, changed, written and freed in turn. Progress and failures are shown; **Cancel** leaves only whole files (results are written under a temporary name, then renamed).
  - **Reading:** photos are turned upright by their EXIF orientation.
- **Combine Images:**
  - **Layout:** side by side, one under another, or a grid of columns.
  - **Look:** aligned, with space between, a border and a background (or none).
  - **Result:** a preview and the result's size, then Open in the editor or Save as.
- **Images to PDF:**
  - **The page:** A4 or Letter as used here, Legal, A3, A5, or each picture's own size; orientation (or landscape for wide pictures); margins; as large as fits, filling, or as big as on the screen.
  - **Several per page:** across × down, with space between.
  - **Smaller file:** pictures stored as JPEG. JPEG files go in as they are either way.
  - **Running:** progress and Cancel, then Open the PDF or Show in folder.
- **The image tools share** a picture list (files, a folder with or without subfolders, or dragged from Explorer; reorder, remove) and remember their choices. They read PNG, JPEG, BMP, GIF and WebP.
- **A PDF destination** for the workflow and Send to:
  - **The file:** a one-page PDF in the capture folder, named like the capture (" (2)" when taken).
  - **Settings › Destinations › PDF:** paper, size on the page, orientation, margins and a Test button.
  - **Recordings** are skipped.
- **Inside.**
  - **Editor:**
    - `ImageEffect` and its effects, `EffectRunner`, `Watermark`;
    - more `ImageOperations` (canvas size, trim, rotate by any angle, flip);
    - the session's preview (`ShowPreview`, `CommitPreview`, `CancelPreview`) and the Area tool;
    - `SpotlightAnnotation` and `MagnifyAnnotation`, `Annotation.MoveFrom`;
    - `TextShaper` and `Bidi`;
    - `PresetLibrary` and `JsonPresetStore`;
    - `ImageCombiner`, `BatchProcessor` and `ImageFiles` (Skia reading and writing, with a 24-bit BMP writer), `PdfWriter` (Skia's PDF backend).
  - **Core:** `PaperSize` and `PageSizes`, `PdfSettings` and `DestinationIds.Pdf`, `AppPaths.PresetsFile`.
  - **Outputs:** `PdfDestination`. Outputs now references Editor for Skia.
  - **App:**
    - `PreviewDialogViewModel` (the Effects window and the small image dialogs);
    - settings as parameters with one template each (`EditParameterTemplates`);
    - `ImageToolWindows`, `IImageFilePicker`, and `IShell.OpenFile`.
  - **Packages:** `SkiaSharp.HarfBuzz` 4.153.1 (MIT; HarfBuzz is "Old MIT"), in THIRD-PARTY-NOTICES.txt.

### Deviations from the spec
- **PDFs are made with SkiaSharp's own PDF writer, not QuestPDF** (the user's choice). QuestPDF 2026.8's Community licence covers only organisations under USD 1M yearly revenue that aren't publicly traded, and it brings 12 MB of its own native Skia. Skia was already in the app.
- **Effects are applied, with undo**, not kept as a live, editable stack on the document (the user's choice). Spotlight and the magnifier are annotations, so they stay movable.
- **Presets have their own file**, `presets.json`, not settings.json (the user's choice): saving one from the editor never races the Settings window's OK. The tools' last choices are kept there too.
- **Add border and Add shadow** became Effects › Border and Drop shadow, with settings. The shadow now comes from the picture's shape (Skia's drop shadow), so it differs a little from Phase 5's fixed shadow. Window captures' own shadow (Settings › Capture) is unchanged.
- **Effects other than a shadow or a border merge annotations into the image**, so they look in the result as they did on screen.
- **Arabic text** wasn't in the spec; the user asked for it during planning.
- **A PDF destination** was added (the user's choice). The batch writes to its folder only, not through destinations.
- **The batch writes PNG, JPEG and BMP** (what the app saves elsewhere), with Skia instead of WIC, so it runs on worker threads. It reads PNG, JPEG, BMP, GIF and WebP.
- **Watermark sizes are in pixels** (or a percentage of the picture file), not relative to each image.

### Fixed during development
- **The magnifier's pixels on screen** differed slightly from the export: the zoom was composed with the view's own transform. The zoomed pixels are now made once on whole image pixels, cached like blur patches, and drawn like the image.
- **A preview could read a freed image.** The Effects window works out previews on a worker thread; closing the editor before one finished freed the image it was reading, and the process crashed (an App test run ended with an access violation). Each preview now works from its own copy of the image.
- **The batch's progress** overflowed a `TimeSpan` on its first report.

### Tested
- **1242 automated tests** at 100% + 100%: 1232 pass and 10 are skipped (the 6 RNNoise tests until `rnnoise.dll` is built, and 4 explicit). Phase 14 had 1117. New:
  - **Editor:**
    - every effect: sizes, transparency, seeds, chosen sides only, colour maths on known pixels, sharpening;
    - effects on an area, and annotations kept or merged;
    - **the preview on screen against the committed image and the export, byte for byte, for every effect;**
    - canvas size, trim, rotate by any angle, flip;
    - spotlight and magnifier (rendering, dragging parts, grips, placement, screen against export);
    - watermarks (place, opacity, tiling, pictures, Arabic);
    - presets (round trip, hand edits, damaged files, cleaning);
    - Arabic shaping, runs of a mixed line, font fallback, right alignment, the box around the ink;
    - combine layouts;
    - the batch (steps, formats, names, failures, at most N at once, cancel, EXIF), and **500 screenshots with flat memory**: 61 MB private peak at 50–200 done, 62 MB at 350–500, in 10.9 s;
    - PDFs (pages, grids, JPEG passed through, cancel).
  - **Outputs:** the PDF destination, **rendered back with Windows.Data.Pdf**: fitted inside the margins of a landscape A4, and actual size on a page the capture's size.
  - **App:**
    - the Effects window's view model: the preview follows the settings, Apply keeps it; **the saved PNG (WIC) has the previewed pixels**; Cancel; errors; effect and watermark presets; the selected area;
    - canvas size and rotate, and the menus;
    - the image tools, and **500 screenshots through the batch view model on a dispatcher: the UI thread never waited more than 37 ms** (2.9 s in all);
    - the Settings PDF section;
    - the new windows on the desktop.
  - **Core:** page sizes, and the PDF settings.
- **The desktop run** (user approved) had one failure, in a new test: the Effects window test never started the first preview, which the editor does before it shows the window. Fixed in the test; both window tests then pass.
- **Not run** (pending in TESTING.md): the new menus, windows and tools by hand in the installed app; Arabic typed with an Arabic keyboard; PDFs opened in other readers; a batch of real photos.

### Known limitations
- **Effects merge annotations** (all but shadow and border), so they can't be edited afterwards; Undo brings them back.
- **The magnifier** shows the picture under its source circle, not the annotations drawn there.
- **Spotlights** are rectangles.
- **Watermarks** keep their pixel size on every picture of a batch, however large the picture.
- **Right-to-left text:**
  - **Ordering** needs Windows' ICU (Windows 10 version 1903 or later); without it each line goes in the direction of its first letter.
  - **Alignment** follows the first letter of the whole text, not each line.
- **The batch:**
  - **Reading:** no TIFF, HEIC or RAW.
  - **Writing:** no WebP.
  - **Taken names:** two pictures that would get the same name are numbered in the order they finish.
- **Combine Images** reads every picture at full size; hundreds of very large pictures need a lot of memory.
- **PDFs:**
  - **Pages:** Skia rounds page sizes to whole points (A4 is 595 × 842).
  - **No extras:** no captions, headers or page numbers, and no adding to an existing PDF.
- **Effects window:** each preview starts from a copy of the image, so very large images (8K and up) preview with a short delay.

## Phase 16 — Timeline Video Editor (2026-10-21)

Recordings can now be edited on a timeline without changing them: cut out parts frame by frame, set each sound track's level, move and resize the webcam, add music, text, arrows, shapes and blurred areas that move with keyframes, speed clips up or ramp their speed, fade and crossfade, freeze a frame, and draw the clicks, keys and pointer from the recording's metadata. The edits are a project (`.cgcap`); **Export** makes a new MP4, WebM, M4A or MP3. The preview is drawn on the graphics card and the export through the very same compositor, so what is seen is what is exported. The phase came in five milestones, each tagged (`phase-16-m1-done` … `phase-16-m5-done`).

### Added
- **The video editor** (tray › **Video Editor** › Open a Recording or Project… or Edit the Last Recording, the recording workflow's **Open in the video editor** step, `--video-editor`):
  - **Opening a recording** makes a project: its size and frame rate, the screen, the webcam placed where it was recorded (hidden when the recording already shows it), a sound track per source (the "Mixed" track left out when the sources are there too), all linked; a recording made with "Keep the video clean" also gets its overlays as effects.
  - **The window:** menus (File, Edit, Insert, View), the preview, the transport (start, a frame back, play/pause, a frame on, end, the time), the timeline with a header per track (mute, solo, shown, lock, a volume slider for sound, Delete the Track), and the selected item's settings on the right. Keys: S, Del, Shift+Del, I, O, Esc, ←, →, Home, End, Space, Ctrl+Z/Y/S/E, zoom.
  - **Closing** asks about unsaved changes, and so does exiting the app.
- **M1 — Trim and cut:**
  - **Split** every clip at the playhead; **cut out a range** from every unlocked track (the gap closes); **delete** a clip with its linked clips, or **ripple delete** it with its time.
  - **The timeline:** click or drag the ruler to scrub, drag clips to move them (Alt: without their linked clips), drag edges to trim; moves and trims snap to edges, the playhead and 0 (Alt: no snapping); a drag is one undo step. Thumbnails along video clips and waveforms in sound clips, made in the background and kept on disk.
  - **Playing:** the sound is mixed ahead on its own thread and the preview follows the sound device's clock (a stopwatch without a device).
  - **Projects:** `.cgcap` files (a zip of `project.json`, stored `media\` with **Save with Media…**); media found where it was or next to the project, missing media offered to be located; undo and redo for every edit.
  - **Export:** MP4 H.264 at 1080p, 720p or the project's size, MP4 HEVC, WebM VP9 or AV1 with Opus, M4A or MP3, at four qualities, on a background thread with progress, the time left and Cancel (no file left). The video encoder is the graphics card's when recording's detection found one, else software. A file already there is only replaced through the Browse dialog; otherwise the export gets " (2)".
- **M2 — Multi-track:** each sound track's volume, mute and solo; each clip's volume (0–200%) and fades; File › **Add Music…** on a track of its own at 30%; pictures (the webcam, or any clip) dragged and resized on the preview, with size, place, shape (rectangle, rounded, circle), border, mirror and opacity in the panel.
- **M3 — Annotations** (Insert menu, Text/Arrow/Blur buttons): text (shaped and ordered like the image editor's, Arabic included), arrows, lines, rectangles, ellipses, highlights, blurred and pixelated areas; over the playhead or the marked range on annotation tracks; fades; dragged, resized and their line ends moved on the preview; keyframes (added at the playhead, or by dragging when there are already two) move them evenly or smoothly.
- **M4 — Speed and transitions:** a clip's speed from 0.25× to 8× (the sound keeps its pitch) or changing evenly along it; crossfades into the next clip; freeze frames (the sound waits); each moves what follows on every unlocked track.
- **M5 — Effects from the metadata** (Insert › Click Highlights, Keystrokes, Pointer Effects): rings at the clicks, keystroke captions, a halo or spotlight around the pointer, and, when the app draws the pointer, a bigger pointer, a smoother path and hiding it when it stands still. They follow each clip's source time, so cuts, speed and freezes keep them on their pictures.
- **Inside.**
  - **New project `CashGear.Capture.VideoEditor`** (no WPF):
    - `Model/`: `VideoProject`, `Track`, `Clip`, `AnnotationItem`, `EffectItem`, `ProjectJson`, `ProjectFile`, `ProjectFactory`;
    - `Editing/`: `ProjectHistory`, `TimelineEdits`, `PlacementEdits`, `TrackEdits`, `AnnotationEdits`, `TransitionEdits`;
    - `Playback/`: `VideoFrame`/`FramePool`, `FrameCursor`, `FrameSource`, `PreviewFrames`, `TimelineMixer`, `PlaybackEngine`, `WasapiAudioOutput`, `MediaPreviews`;
    - `Rendering/`: `FrameCompositor`, `MetadataOverlays`;
    - `Timeline/`: `TimelineView`, `TimelineSession`, `TimelineRenderer`;
    - `Export/`: `ExportPresets`, `ProjectExporter`.
  - **Media:** `VideoStreamReader` (frame-exact seeks, Direct3D 11 hardware decoding through one shared device), `AudioStreamReader` (48 kHz stereo, sample-exact seeks), `TempoFilter` (atempo), `ExportWriter` (MP4, WebM, M4A, MP3), Opus and MP3 in `AudioEncoder`, frame rate and sound details in `MediaStreamInfo`.
  - **Core:** export-only encoders (libvpx-vp9, libsvtav1, libkvazaar) and `EncoderPresets.Usable`/`ForExport`; `AppState.VideoEditor`; `DestinationIds.VideoEditor`; `InstanceCommand.VideoEditor`.
  - **App:** `VideoEditorWindow`, `PreviewHost` (SKGLElement, falling back to SKElement), `TimelineControl`, `VideoEditorViewModel`, `ItemProperties` (the panel, on the image editor's parameter templates; a `NoteParameter` template), `ExportWindow`, `VideoEditorWindows`, `VideoEditorDestination`; the tray's Video Editor menu; the last recording remembered.
  - **Tests:** a new `CashGear.Capture.VideoEditor.Tests`; `StampedRecording` writes test recordings whose every picture carries its number.

### Deviations from the spec
- **The export draws every frame with the preview's Skia compositor and encodes it in the app** (the user's choice), instead of building an FFmpeg filter graph: text (Arabic too), blur and keyframes look the same in both, and cuts are exact to the frame. The sound is mixed in C#; FFmpeg's atempo keeps the pitch at other speeds.
- **The GPU preview uses OpenGL** (SkiaSharp's `SKGLElement`, the user's choice of a GPU preview with a fallback), not a Direct3D Skia context: WPF can host OpenGL through SkiaSharp.Views.WPF without new packages. Decoding is on the graphics card (D3D11VA); each picture is then copied to memory and converted to BGRA, not kept as a GPU texture.
- **Undo keeps snapshots of the project** (records are immutable), with the image editor's rules for merging and the saved marker, rather than a command per kind of edit.
- **Crossfades are overlaps** made by Crossfade into the Next Clip (at most half the shorter clip), not transition objects; only a cross-dissolve.
- **A speed ramp is an even change** from a start to an end speed per clip, not a curve; its sound is resampled, so its pitch follows the speed (constant speeds keep their pitch).
- **No Edit button on the "Recording saved" notification:** tray balloons have one click action (it shows the file). Tray › Video Editor › **Edit the Last Recording** takes its place (as planned with the user), with the recording workflow's step, off by default.
- **Recordings made with "Keep the video clean" open with their overlays as effects** (not in the spec), so they look as recorded and can be changed.
- **The GIF preset** is left to Phase 17, as the spec says.
- **HEVC exports** have sizes in multiples of 8 (Kvazaar, the software fallback, takes no other).
- **The VideoEditor project targets Windows** (it plays sound through NAudio's WASAPI), unlike Editor and Media.

### Fixed during development
- **Exports stopped with "Encoding sound failed: Invalid argument":** the sound was encoded up to each video frame, which left short AAC frames in the middle. Only the last frame of an export may be short now.
- **Kvazaar refused 320 × 180** (not a multiple of 8): HEVC exports round their size to 8.
- **Small pictures on the preview could only be resized:** the resize grip covered all of a 45 px webcam. It takes at most a quarter of a picture now.
- **A click just inside a clip's end picked the next clip's start** (items were tested last to first): the item under the pointer comes first now.
- **A recording opened with its pictures below its sound tracks** when the project had no picture track yet: the screen and webcam now go above the sound.
- **atempo gives up to 0.6% fewer samples** than the speed asks (0.25×: 7.95 s for 8 s): the mixer fills or cuts each clip's sound to its exact length on the timeline.
- **The preview was drawn about 90 times a second while playing** (every picture decoded ahead asked for a draw): it is drawn when the playhead reaches a new frame, or when a picture it went without arrives, 30 times a second for a 30 fps project.
- **The 1080p preview acceptance test showed 290 of 300 pictures** when it ran beside the 20-minute export test: the two timed acceptance tests now run on their own, after the rest of their assembly.

### Tested
- **1358 automated tests** without the desktop: 1261 pass and 97 are skipped (the desktop tests, which need approval to run, the 6 RNNoise tests until `rnnoise.dll` is built, and the explicit ones), 0 fail. Phase 15 had 1242; the 116 new ones are 73 in the new VideoEditor test project, 23 in App, 19 in Media and 1 in Core.
- **Acceptance (automated):**
  - **M1:** three cuts (100 s, 10.5 s and 60 s) out of a synthetic 20-minute recording (320 × 180, 30 fps, every picture numbered, a flash and a 1 kHz beep every 2 s) exported to MP4: the duration is right to the frame (1029.5 s, 30,885 frames), every frame is the source frame the timeline shows then, and every flash has its beep within 0.1 ms (515 pairs). It made the recording in 43 s and exported it in 66 s.
  - **Preview:** a 10 s 1080p30 recording decoded on the graphics card and drawn 60 times a second into a 1280 × 720 view on the playback engine's clock: 299 of the 300 pictures shown, no draw waited for a picture, 8.9 ms a draw on average.
  - **Re-open:** a project with every kind of item saved and opened again reads back as the same JSON, also moved with its media, with missing media located, and with its media inside.
- **Media:** frame-exact seeks forward and back with hardware and software decoding, hardware pictures within 3 levels of software; keyframes only; scaled reads; sound seeks landing on the same sample each time; 44.1 kHz resampled; atempo lengths and pitch; each container probed with ffprobe.
- **VideoEditor:** splits, cuts, deletes, moves and trims (with links and locks), time maps with speed, ramps and freezes; the history; compositor pixels (letterbox, webcam circle, border, fades, crossfades, annotations, text identical to the image editor's in English and Arabic, blur and pixelate); the mixer (volumes, solo, fades, beeps on their samples across cuts, music, speed and ramp pitch, silence under a freeze); the playback clock; the timeline's geometry and drags; exports frame by frame after cuts, a 2× part and a freeze, with the beeps where they belong; every preset; cancelling; metadata effects against the overlay planner, after a cut, a bigger and a hidden pointer, smoothing.
- **App:** the editor's view model with stamped recordings (frames at the playhead, every command, the panel's settings and their undo steps, dragging on the preview, music, tracks, annotations and keyframes, speed, freeze and crossfade, effects), the export window's view model, the workflow step and the switch; desktop tests for the windows.
- **The desktop tests of the video editor** (user approved, this group only): the window with the preview on the graphics card and in memory, each playing a second (30 frames drawn, decoding on the graphics card), closing with unsaved changes, the export window: 4 of 4 pass. The first run failed in the tests' set-up: they wrote their test recording on the STA thread, which Media Foundation's encoder refuses; it is written on a worker thread now.
- **Not run** (pending in TESTING.md): the other desktop tests (unchanged since Phase 15, where they passed); the editor by hand in the installed app; the GPU preview on other graphics cards and under Remote Desktop; real recordings with a webcam and metadata; long exports with the graphics card's encoders.

### Known limitations
- **Preview:**
  - Each picture is copied from the graphics card and converted on the processor, at the project's size at most: a 4K project previews more slowly than 1080p, and speeds above 4× can stutter (every picture is decoded).
  - The sound plays on Windows' default output device only.
- **Editing:**
  - Annotations: no freehand, step numbers, spotlights or magnifiers; keyframes move and resize them but don't change colour or opacity; text is resized by its font size.
  - Crossfades and fades are linear; a ramped clip's sound changes pitch.
  - Effects need the recording's metadata file; a bigger, smoother or hidden pointer only when the pointer isn't in the pictures; click sounds aren't added.
- **Projects:** media is found by its path (or next to the project); Save with Media copies whole recordings into the file.
- **Export:** the whole timeline only (no range); one mixed sound track; no 10-bit or HDR; GIF in Phase 17.
- **Disk:** thumbnails, waveforms and media unpacked from projects stay in `%LocalAppData%\CashGear\Capture\VideoEditor\` until deleted by hand.

## Phase 17 — Auto-Zoom, Captions, GIF (2026-10-21)

The video editor can now zoom into the screen where things happen, the way Screen Studio does: zooms made from the recording's clicks and typing that follow the pointer smoothly, and zooms of your own on the timeline. Captions come from the speech in a recording, on this PC with Whisper (English and Arabic among others), or are typed or imported; they are drawn into the export (Arabic right to left) and saved as SRT or WebVTT files. Exports can be of the marked range alone, and of a new kind: animated GIFs, also made by a recording-workflow step.

### Added
- **Zooms** (Insert › Zoom, the Zoom button): the screen's pictures zoom in (2× by default, 1.25× to 4×) over the zoom's time and back out, smootherstep easing about the one point that stays in place, so they start and stop without a jolt. A zoom follows the pointer from the recording's metadata (moving only when the pointer nears the edge of the middle half of the view, on two springs in a row) or stays on a place. Zooms close together pan from one to the next without zooming out. The view never leaves the screen's picture, so no bars show. The clicks, the pointer and its halo or spotlight from the metadata zoom with the pictures; the webcam, annotations, keystroke captions and captions don't.
  - **Editing:** select a zoom and the preview shows the whole picture with the zoom's frame: drag it to place the zoom (it then stays there), its corner to zoom more or less. The panel sets the zoom, following the pointer or not, the place, and how long it takes to zoom in and out. Zooms are cut, moved and trimmed like any item.
  - **Auto-Zoom** (Insert › Auto-Zoom from Clicks and Typing): clicks and typing close in time (2.5 s) and in place (within one view at 1.25× or more) become a zoom that starts 0.8 s before them and ends 1.5 s after, as close as 2× allows, following the pointer, on a "Zoom" track; typing counts where the last click was. They are read at each clip's source time, so cuts and speed changes are taken into account. Running it again replaces its own zooms and keeps the ones you changed. Without metadata it says why.
- **Captions** on caption tracks:
  - **From speech** (Insert › Captions from Speech…, the Captions button): a sound track (the microphone first, or all of them) is mixed as it is heard on the timeline, taken down to 16 kHz and transcribed with Whisper, on the graphics card when it can (Vulkan) or the processor, with progress and Cancel. The language is found out from the speech or set (English, Arabic). Models: Base (57 MB), Small (181 MB, the default, and the least for Arabic), Medium (514 MB), downloaded the first time one is used, checked, and kept. The captions come back as a new caption track named for the language, in one undo step: at most two lines of 42 letters and 7 s each, cut between words by the words' times and at the ends of sentences. Only the marked range, when one is marked. The sound never leaves the PC.
  - **By hand** (Insert › Caption) and from files (File › Import Captions… for SRT and WebVTT); File › Save Captions… writes them.
  - **Editing:** the text (typing it drops the words' times), split at the playhead (by the words' times, or at the nearest space), merge with the next; cuts through a caption keep the words said on each side. The track's look: font, size, bold, colours, a box behind each line, top or bottom, how wide the lines may be.
  - **Burned in:** each line drawn as the image editor draws text (HarfBuzz shaping and right-to-left ordering, so Arabic joins and reads right to left), wrapped to the width in the words' order, centred, while the track is shown.
- **Export** (all formats): **only the marked range**; **burn the captions in** or leave them out; **save them as SRT and/or WebVTT** beside the file (times from the range's start). The choices are remembered.
- **GIF export**: a GIF preset in the export window with its own choices instead of the quality: frames a second (15), width (800 px, never wider than the project), dithering (a fine pattern, best for screens; diffusion, for gradients and video; none), a size to stay under (made again with fewer colours, then fewer frames, then fewer pixels until it fits) and whether it plays over and over. FFmpeg makes the palette from every frame (`palettegen`), maps each frame to it (`paletteuse`, only the area that changed) and leaves what didn't change transparent. A 10 s screen recording at the defaults is about 1 MB.
  - **The recording workflow's Make a GIF step** (Settings, off by default) makes `name.gif` beside each recording with the export window's GIF choices.
- **Inside.**
  - **VideoEditor:** `ZoomItem` and `ZoomCamera` (a pure function of the project and the time; the springs are worked out from the start of their zooms at 240 steps a second and kept, so the preview, seeks and the export see the same camera), `AutoZoom`, `CaptionItem`/`CaptionStyle` and `TrackKind.Caption`, `CaptionEdits`, `CaptionRenderer`, `SubtitleFiles`, `ProjectRange`, `TranscriptAudio`, the GIF preset and `GifOptions`; `FrameCompositor` draws the screen through the camera with a float matrix; `ExportRequest` has a range, burn-in, caption files and GIF options.
  - **New project `CashGear.Capture.Transcription`:** `WhisperModels` and `WhisperModelStore` (download, SHA-256, `.partial`), `WhisperTranscriber` (Whisper.net 1.9.1 with its CPU and Vulkan runtimes).
  - **Media:** `GifWriter` (two passes through avfilter and the GIF encoder and muxer); `ExportContainer.Gif`.
  - **Core:** `TranscriptSegment`/`TranscriptWord`; `DestinationIds.Gif`; the caption, transcription and GIF choices in `VideoEditorState`.
  - **App:** `TranscribeWindow`/`TranscribeViewModel`, the caption and zoom panels, the export window's range, caption and GIF choices, `GifDestination`; the transcriber and the model store in the host (models in `%LocalAppData%\CashGear\Capture\Models\Whisper`, or a `models` folder next to the app).
  - **Tools:** `tools\get-whisper-models.ps1` downloads the base and small models for the tests into the git-ignored `tools\whisper`; `tools\package.ps1` ships only the win-x64 Whisper runtimes.
  - **Tests:** a new `CashGear.Capture.Transcription.Tests`; `StampedRecording` can draw its own pictures (`Picture`), for a screen-like recording; `GifProbe` reads GIFs with ffprobe.

### Deviations from the spec
- **No gifski** (the user's choice): gifski is AGPL-3.0, which doesn't suit an app that may be sold. FFmpeg's `palettegen` and `paletteuse` do it in-process with the avfilter library already shipped (LGPL), with dithering choices and a size target; there is no fallback because there is nothing else.
- **What zooms:** only the screen's pictures (video tracks) and the effects that mark a place in them (clicks, the pointer, its halo or spotlight). The webcam, annotations, keystroke captions and captions stay where they are on the canvas, so the preview is what you edit and a title doesn't fly off; a blurred area stays where it is on the canvas, so check it while zoomed.
- **Zoom regions are items** on effect tracks (with scale, focus and easing) rather than raw keyframes; the camera's motion is worked out from them.
- **Models are quantized** (q5_1 for Base and Small, q5_0 for Medium: a third of the size, about the same accuracy) and come from whisper.cpp's own Hugging Face repository, checked against their SHA-256.
- **The graphics card through Vulkan, no CUDA** (the user's choice): Vulkan works with NVIDIA, AMD and Intel cards without the CUDA toolkit; `ggml-vulkan-whisper.dll` adds about 58 MB to the app. Whisper.net loads one runtime per process, so "Use the graphics card" chooses the device (the Vulkan build runs on the processor too), not the DLL.
- **The Arabic acceptance test** burns a fixed Arabic transcript into an export and reads it back with Windows OCR (this PC has no Arabic voice to make speech from, the user's choice). Whisper on real Arabic speech is a by-hand check.
- **A marked range can be exported in every format**, not only as a GIF (it was missing since Phase 16).
- **Captions are burned in while their track is shown**, and the export can leave them out; no soft subtitle streams inside the MP4 or WebM (files beside them instead).

### Fixed during development
- **An SRT file read back lost text:** "Tom & <Jerry>" came back as "Tom &" (anything in angle brackets was taken for a tag). Only SRT's and WebVTT's own styling tags are taken out now, and times are read in whole milliseconds, as written, so they come back exactly.
- **The last frame of a GIF lasted a hundredth of a second** (its frames had no duration): each frame lasts 1/fps, and a 10 s GIF lasts 10 s.

### Tested
- **1395 automated tests** without the desktop: 1298 pass and 97 are skipped (the desktop tests, which need approval to run, the 6 RNNoise tests until `rnnoise.dll` is built, and the explicit ones), 0 fail. Phase 16 had 1358; the 37 new ones are 18 in VideoEditor, 10 in App, 6 in the new Transcription project and 3 in Media.
- **Acceptance (automated):**
  - **Auto-zoom at 60 fps:** a zoom following a pointer that waits, glides, waits and jumps, drawn 600 times through the compositor at 60 fps with a white square in the pictures: the square is where the camera puts it to 0.126 px at worst (no rounding to whole pixels), its speed changes by 0.68 px per frame² at most, the measured path follows the camera's to 0.22 px per frame², and its acceleration never turns back and forth (0 reversals).
  - **Arabic captions burned in:** an 18-word Arabic transcript as one caption of two lines on a 1280 × 720 recording, exported to MP4, decoded and read by Windows OCR (Arabic): 18 of 18 words, in order, the first words on the first line.
  - **GIF:** a 10 s, 1280 × 720 screen-like recording (a window with text over a gradient, typing, a moving pointer, a scroll) exported at the defaults: 0.96 MB (under 5 MB), 800 × 450, 150 frames, 10.00 s, looping, made in 5.6 s. Asked to stay under 60% of that, it came to 0.35 MB at 10 fps, 600 px and 128 colours.
- **Transcription:** English said by Windows' own voice (made in memory, never played) heard word for word by Base (language found out: English) and Small, on the processor and on the GTX 1650 through Vulkan; word times in order; cancelling; tokens that don't spell their text give no words; the model list matches the download script; downloads from a local server (progress, checksum refused, cancelled, kept once, found in another folder). A 7 s clip took 2.9 s on the processor and 6.9 s on the graphics card (its set-up dominates short clips).
- **VideoEditor:** zooms saved and sliced, the camera's eases, clamping inside letterboxed pictures, pans between zooms, the same camera whatever the order of the frames, click rings moving with the zoom and keystrokes staying put, auto-zoom through a cut and at 2×; captions from transcripts (line limits, sentence ends, word pieces, letters), split and merge, cuts through captions, SRT and WebVTT both ways (Arabic, over an hour, other writers' files), burned-in pixels identical to the image editor's text, Arabic wrapped in order; a range exported with its SRT and WebVTT; the 16 kHz sound of chosen tracks; GIFs of a range, looping or not.
- **Media:** GifWriter's frames, rate and loop; fewer colours and no dithering make smaller files; writing before the palette is refused.
- **App:** zooms inserted, set in the panel and dragged on the preview; Auto-Zoom with and without metadata; captions from speech with a fake transcriber (choices remembered, a range, nothing heard), captions typed, split, merged and styled, imported and saved; the export window's range, captions and GIF choices; the Make a GIF step.
- **Build and publish:** `dotnet build --no-incremental` without warnings. A Release win-x64 publish carries Whisper's runtimes for every platform (121 MB); `tools\package.ps1` now keeps win-x64's, for the processor and Vulkan (59 MB). The installer itself wasn't built.
- **Not run** (pending in TESTING.md): the desktop tests (this phase's changes to the windows were checked through their view models); Whisper on real speech, Arabic above all; Vulkan on AMD and Intel cards; the new features by hand in the installed app.

### Known limitations
- **Zooms:** the webcam, annotations and captions aren't zoomed, and a blurred area doesn't follow zoomed pictures. One camera for all the video tracks. Following the pointer needs the recording's metadata. While a zoom is selected the preview shows the whole picture (deselect it to see the zoom).
- **Captions:** Whisper's word times are close but not exact (a tenth of a second or two); Arabic segments usually come without word times, so their captions are split by letters. One look per caption track. No word-by-word highlighting, no soft subtitle streams inside the files, styling in imported files is dropped.
- **Transcription:** the first use of a model downloads it (57 to 514 MB). The window offers English and Arabic (and finding out), though Whisper knows more. No translation. The runtime is chosen once per run of the app. Small and Medium are slow on the processor for long recordings.
- **GIF:** no sound; at most 256 colours (diffusion dithering for photos and video); the size limit tries four times at most and keeps the last.
- **Export:** the marked range is the one when the export window opened.

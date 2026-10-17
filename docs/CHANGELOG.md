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

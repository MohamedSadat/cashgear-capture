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

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
| Dual monitor 100% + 100% (secondary right of primary, offset Y) | Phase 0–1 pass (2026-10-14) |
| Dual monitor, mixed DPI (100% + 150%) | **pending** — see below |
| Monitor left of primary (negative X) | **pending** — see below |
| Windows 10 22H2 | Phase 0–1 pass (2026-10-14) |
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

### Pending: mixed DPI and negative coordinates

These need display-setting changes, so they are left for a manual run:

1. Settings → Display: set DISPLAY1 to 125% or 150%. Restart CashGear Capture.
2. Check the log: DISPLAY1 shows `dpi=120` / `dpi=144` with unchanged physical bounds.
3. Capture Full Screen: `DISPLAY1.png` must still be 1280×1024 and `_all.png` 3200×1082.
4. Drag DISPLAY1 to the left of the primary. Restart, capture again: the log shows DISPLAY1 at a negative X, and the images keep the same sizes with DISPLAY1 on the left of `_all.png`.
5. Run `dotnet test --solution CashGear.Capture.sln` in each layout (the interactive tests re-derive expectations from the live layout).
6. Restore the original layout.

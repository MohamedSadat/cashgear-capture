# Testing

## Automated

```powershell
dotnet test CashGear.Capture.sln
```

Tests tagged `Category=Interactive` need a logged-in, unlocked desktop. They skip themselves when none is available or when `CASHGEAR_SKIP_INTERACTIVE=1`.

## Manual test matrix

Record results per phase below.

| Configuration | Status |
|---|---|
| Single monitor 100% | |
| Single monitor 150% | |
| Dual monitor, mixed DPI (100% + 150%) | |
| Monitor left of primary (negative X) | |
| Windows 10 22H2 | |
| Windows 11 23H2+ | |

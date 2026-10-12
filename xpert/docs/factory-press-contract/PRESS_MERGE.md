# Factory Press → dac-rewrite merge (2026-10-12)

Source zip: `dac-FactoryPress-source-for-Codex_20261012-160656`  
Extracted: `/tmp/factory-press-src/…`  
Target: `/workspace/dac-rewrite/`  
**No sync to user PC** (this round).

## Canonical semantics (PATHS + PressTestRunner + StatusCopy)

| Gate | Pressed (stableKind=2) | Released (kind=1) |
|------|------------------------|-------------------|
| Calibrate (ChannelA Rapid) | Allowed | Denied |
| Non-zero press / AO | Denied (AO=0 OK) | Allowed |
| FakeLive smoke | Auto-press for calib; auto-release for press | — |

Shared Acquisition is the single source of truth. DesktopUi / HostAdapters use the same StatusCopy / OperationalStatusBoard gates (E-stop, HVON/lamp via Live, range auto via Task6RangeAdvisor + PressTestRunner, cleanup gate, DeviceSdkLease, ArtFreshFeedbackWindow, PicoCaptureCooldown).

## Taken from zip (overwrote rewrite)

| File | Why |
|------|-----|
| `Xiongxin.Owts.HvPressTest/FactoryPressForm.cs` | Stable in-form「校准采集」+ `stableKind==2` button gate; press toggle; 80k locate preview path |

## Already present / kept from rewrite (zip SAME or rewrite newer-correct)

| Area | Decision |
|------|----------|
| `PressTestRunner.cs`, `FactoryPressApi.cs`, `PdCalibrationStore.cs`, `PressTestConfig.cs`, `BasicMetricsCalculator.cs`, `WaveformSummaryWriter.cs` | **SAME** as zip — already stable |
| `Acquisition/Live/*` except ArtPico note below | **SAME** — CalibrationPulse*, lease, cooldown, FakeLive, EmergencyMonitor, Task6RangeAdvisor |
| `Contracts/Ui/StatusCopy.cs`, `OperationalStatusBoard.cs` | **SAME** — CanEnterCalibration needs pressed; CanEnergize needs released |
| `Live/ArtPicoLiveBackend.cs` | **Keep rewrite** — Rapid wait loop must **not** Abort on E-stop pressed (calib requires pressed). Zip still aborted on hard-read pressed; that conflicts with PATHS |
| `FactoryPdHighPass.cs` + `PdBandpassFilter.cs` | **Keep rewrite** — PATHS asks HVX bandpass handoff @ 10 kHz; zip still had FIR-only |
| `WaveformPlotPanel.cs` | **Keep rewrite** — column-peak histogram (dense preview); not press/range logic |
| `Program.cs` + `FactoryCalibrationForm.cs` | **Keep rewrite** — optional `--calibrate-ui` / 雄新校准采集.exe; main Form uses in-form calib |
| `vendor-handoff/HVX_PD_BandpassFilter_handoff/` | Copied from zip if missing (reference) |

## Brand

雄新电力 · 雄新 OWTS（FactoryPressApi / Program / Form title）。

## Verify

```bash
dotnet build Xiongxin.Owts.sln -c Release
dotnet run --project Xiongxin.Owts.HvPressTest -c Release -- --smoke --gain 6000
```

## Smoke result (box, Asia/Shanghai)

- `dotnet build Xiongxin.Owts.sln -c Release` → **0 errors** (2 unused-field warnings in Form demo fields)
- `dotnet run --project Xiongxin.Owts.HvPressTest -c Release --framework net8.0 -- --smoke --gain 6000` → **OK exit=0** completed=2
  - FakeLive: calib with E-stop pressed → auto-range → Rapid → interval filter → press after release

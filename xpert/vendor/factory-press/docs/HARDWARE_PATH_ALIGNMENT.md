# Hardware path alignment (DesktopUi ↔ Factory Press)

**Date:** 2026-10-12 (Asia/Shanghai)  
**Brand:** 雄新电力 · 雄新 OWTS  

## 2026-10-12 URGENT：量程调节 + 定位数据提取对齐出厂加压

### Root causes（用户报 WRONG 的两点）

| 问题 | 根因 | 修复 |
|------|------|------|
| **量程调节** | `RunFactoryAlignedPress` 曾每轮 `initialRangeIndex=6` 起爬，无 `_lastSuccessfulRangeIndex` / 同压沿用 / 拒盲目降档 / 单次降档≤2 钳制；未记住 `PicoRangeIndexUsed` | 与 `PressTestRunner` 同包：`ResolveStartingRangeIndex`、同压强制沿用、`RememberSuccessfulRange`、拒绝低于已成功档的降档、钳制≤2、`ApplyRangeToLive`；成功写 `LastPressCycle` |
| **定位数据提取** | (1) DesktopUi `RunAnalyze` 喂 **demo 波形**，不用加压周期；(2) `AcquisitionCyclePersister` 预览用 `n/previewMax` 抽稀，有效 Fs 可 **&lt;20 kHz**，破坏 10 kHz HP；缺 firstZero/Task6/suggested meta | `FactoryLocateExtract`：ADC→V（×pC/V）+ 出厂峰值仓 80k/≥20k；`RunAnalyze` 优先 `LastPressCycle`；Persister 预览与 `WaveformSummaryWriter` 同规则并写 `picoEffectiveRateHz` |

**未改：** E+ / Task17 数学；DesktopUi GUI 布局；不 sync 用户 PC。

### Files（本轮）

- `HostAdapters/OwtsServices.cs` — factory range memory/clamp/reject + `BuildLocateRequestFromPressCycle` / `LastPressCycle`
- `HostAdapters/FactoryLocateExtract.cs` — **new**
- `HostAdapters/AcquisitionCyclePersister.cs` — 80k/≥20k peak-hold preview + meta
- `DesktopUi/MainForm.cs` — wiring: Analyze ← LastPressCycle（布局不动）
- `tests/.../Phase1.Smoke` — 量程记忆 + locate extract + persister Fs 断言
- `docs/HARDWARE_PATH_ALIGNMENT.md` / `next-gaps.md`

### Remaining unknowns（需用户真波形）

- 实机 Task6 建议升/降档是否与 FakeLive 同触发；现场过小峰是否仍被误降档
- 加压 LocData 用 V 还是 pC 与现场 Task17 标定一致性（默认 × `PdCalibrationPcPerVolt`，缺省 2000）
- 超长 raw（60 ms×125 MS/s）定位是喂满采样还是仅 ≥20k 预览 — 当前：短波全速率，长波峰值仓保 Fs≥20k

---

## Canonical hardware stack (single source of truth)

| Concern | Implementation |
|---------|----------------|
| Session | `SingletonAcquisitionSession` → `LiveAcquisitionSession` |
| Lease | `DeviceSdkLease` (process-owned; clean Close allows reconnect) |
| Pico | Single Open; `SuppressPicoStartupWindow`; `PicoCaptureCooldown` between rounds |
| ART feedback | `ArtFreshFeedbackWindow` after AO |
| E-stop gates | `StatusCopy` / `OperationalStatusBoard`: **pressed = calib / AO=0 only**; **released = non-zero press** |
| Abort | HV OFF + AO=0; Pico sync capture on press abort; UI stop = `RequestOperatorAbort` (no permanent latch) |
| Cleanup gate | Only true ClearTask / CloseUnit failure; operator ack to reset |
| Calib (Live/FakeLive) | `CalibrationPulseAcquisitionRunner` (ChannelA Rapid, auto-range, interval filter) |
| Press | `RunCycleAsync` + Task6 range advise + auto-range retry + inter-round Pico cooldown |

## Audit: divergences found (pre-fix)

| # | Divergence | Impact |
|---|------------|--------|
| D1 | `OwtsServices.BuildLiveHardwareOptions` always Short profile; Live lacked `ApplyPicoOscillationWindow60Ms` | Field capture window ≠ factory press |
| D2 | `FakeLiveHardwareBackend()` created **without** shared `LiveHardwareOptions` | Sample/resolution drift vs session |
| D3 | `Live.Open` resets `BenchCalibrationApplied` from session options; DesktopUi Live often left false after file Apply | Non-zero energize wrongly blocked / gain lost |
| D4 | DesktopUi「校准一轮」used `RunCycleAsync(0V background)` not Rapid pulse path | Calib hardware path ≠ factory press |
| D5 | DesktopUi continuous press: no auto-range retry, no explicit inter-round `PicoCaptureCooldown` wait | Fluency/stability under multi-rep |
| D6 | Hard over-current default 1.0 A vs factory bench **0.012 A** when calibrated | Safety trip threshold mismatch |

Already aligned before this pass: Singleton Open, E-stop StatusBoard gates, OperatorAbort, cleanup gate, FakeLive Create path, lease/cooldown primitives inside Live session.

## What changed (this pass)

| File | Change |
|------|--------|
| `HostAdapters/OwtsServices.cs` | Factory-aligned `BuildLiveHardwareOptions`; FakeLive backend shares `hw`; `AlignHardwareAfterOpen`; `RunFactoryAlignedCalibration`; `RunFactoryAlignedPress` (+ result DTOs) |
| `DesktopUi/MainForm.cs` | **Call-site only** (no layout): Connect → `AlignHardwareAfterOpen`; Live/FakeLive calib/press → factory-aligned APIs; simulation keeps prior RunCycle path |
| `tests/.../Phase1.Smoke/Program.cs` | Extend DesktopUi FakeLive smoke to exercise factory-aligned calib + 2× press |
| `docs/HARDWARE_PATH_ALIGNMENT.md` | This document |

**Not changed:** MainForm visual design; location E+ / Task17 / AHistory / Algorithms mapping; HvPressTest Form UI.

## Verify

```bash
dotnet build Xiongxin.Owts.sln -c Release
dotnet run --project Xiongxin.Owts.HvPressTest -c Release --framework net8.0 -- --smoke --gain 6000
dotnet run --project tests/Xiongxin.Owts.Phase1.Smoke -c Release
```

## Still open (needs user decision)

- DesktopUi FakeLive default bench gain remains **2000** (demo) vs factory press smoke often **6000** — intentional unless field asks to unify.
- Simulation mode calib still 0 kV `RunCycle` (no Rapid) — acceptable; Live/FakeLive use factory path.
- Optional: expose factory-aligned progress on existing log pane only (already AppendLog); no new controls.


## Field fluency (2026-10-12 follow-up)

| Item | Result |
|------|--------|
| 连接后可编增益 | **Done** via existing MainForm「台架标定…」→ Apply；`TryApplyBenchCalibration` sets gain + `FactoryBenchHardOverCurrentA` without reconnect. Button enabled when session Open. |
| 起停合并 | **Verified/hardened**: `ResetStopTokenForNewBatch` each press/calib；Stop → `RequestOperatorAbort` + try clear soft latch；0V already skips HVON/HVUP inside `LiveAcquisitionSession`. |
| pd-calibration.json | **Done**: HostAdapters `PdCalibrationStore` loads `{AppBase}/config/pd-calibration.json` on Align/Connect；invalid/missing → default **2000** pC/V (`OwtsServices.PdCalibrationPcPerVolt`). Same JSON schema as FactoryPress. No new UI. |
| GUI freeze | No new controls; no MainForm redesign. |
| E+/Task17 | Untouched. |


## Dual serial identity + long session (2026-10-12)

| Item | Behavior |
|------|----------|
| Dual ID | `HardwareDualIdentity` + **Open hard gate**: if `ExpectedArtIdentity` / `ExpectedPicoSerial` (or Open `ArtSerial`) configured, mismatch → Open rolls back (P0.5 low, lease released, clear error). Unconfigured → skip (not blocking). |
| FakeLive | Deterministic `OpenedArtDeviceName` / `OpenedPicoSerial`; mismatch Open throws; match Open OK. |
| Live ArtPico | Records `OpenedArtDeviceName` / `OpenedPicoSerial` after OpenUnit; same hard gate. |
| Long session | Singleton: calib+press share one Open; second Open denied; clean Close → lease reclaim → Connect again; P0.5 high while connected, low on Close. |
| Idle DI rebuild | Only when not `_cycleRunning`/`_calibrating` and HV/AO off — does not fight press/calib. |
| Pico mid-run | Normal path never re-OpenUnit; `TryReopenPico` only last-resort Pico-comm recovery (not routine). |

Field blockers needing real hardware: ART USB3135 + Pico 5242D with configured `OWTS_EXPECTED_*` / `OWTS_PICO_SERIAL`; verify P0.5 lamp and OpenUnit serial match on the target PC.

## Smoke results (box, Asia/Shanghai 2026-10-12)

- `dotnet build Xiongxin.Owts.sln -c Release` → **0 errors**
- DesktopUi WinForms cross-compile (`OwtsCompileWinFormsOnNonWindows=true`) → **0 errors** (MainForm call-site OK)
- `HvPressTest --smoke --gain 6000` → **OK exit=0** completed=2
- `Phase1.Smoke` → **PHASE1_SMOKE_OK** including `FactoryAlignedCalibration` + `FactoryAlignedPress×2`
- Field fluency follow-up: Phase1 **PHASE1_SMOKE_OK** (pd-cal default 2000, Apply gain+HardOC without reconnect, stop→repress); HvPressTest smoke OK.

# 加压调用栈对照 — Xpert 实际路径 vs 出厂加压程序（金标准）

构建戳见 `NOTES_DAC_REBUILD.md`。

**范围（硬性）：** 只对齐硬件调用方式、流程、错误处理。加压测试 GUI 外观/交互布局不改；出厂桌面首页的 R//、Imax、电容公式指标层不接入 Xpert。

## 权威对照

| 来源 | 用途 |
|------|------|
| `dac-FactoryPress-full-src-for-Codex_20261012-201121`（四项目） | 硬件实现：`OwtsServices.RunFactoryAlignedPress` / `LiveAcquisitionSession` |
| `HV-DAC_Xpert_V1.1.2_FactoryPressMerge_*` / `XpertFactoryPressBackend` | Xpert 侧金标准：`Open` → `PressRound` |
| 雄新出厂加压测试.exe | 现场可靠硬件联动参照 |

**唯一入口：**  
`XpertPressBridge` → `OpenForXpert` / `RunRoundAsync` → `OwtsServices.RunFactoryAlignedPress`；急停 `PollEmergencyStage`。  
**禁止：** 旧 `Pressure.Runtime` `Capture*` / `Monitor*`、第二路 `MonitorAsync`、`FactoryPressOwts` / `ShowForXpert`。

## 工厂硬件路径

```
Connect / Open
  → DeviceSdkLease + LiveAcquisitionSession.Open
  → LiveEmergencyMonitor（P0.4）
  → TryApplyBenchCalibration（增益/分压；HardOC=0.012）
  → RampStepAo ≈ clamp(60/gain, 0.002, 0.1)
  → AbsoluteOverVoltageLimitV = maxKv×(1+trip)
  → PreparePressBatch（清软锁存；真急停/未知仍拒）
  → CanEnterNonZeroPressure（五态门禁）
  → RunFactoryAlignedPress（Task6RangeAdvisor）
  → SessionFault 锁存 + StatusCopy
```

## Xpert「加压测试」当前路径（与 PressRound 对齐后）

```
ModuleHost.EnsureFactoryPressSession
  → OpenForXpert(art, pico, fake, PressureRatio, DividerRatio, MaximumVoltageKv, RangeIndex, pdCal)
      · 存 _maximumKv；清 _requiresReopen
      · CreateLive|CreateFakeLive → Open（DeviceSdkLease / LiveEmergencyMonitor / SessionFault）
      · TryApplyBenchCalibration；FactoryRampStepAoVolts；AbsoluteOverVoltageLimitV
      · ApplyXpertPdCalibrationPcPerVolt；MarkDischargeCalibrated
  → 急停：PollEmergencyStage only（禁止 MonitorAsync 回退）
  → 每轮 RunRoundAsync（同 XpertFactoryPressBackend.PressRound）
      ① targetKv ∈ [0, _maximumKv] 否则 NotReady
      ② targetKv>0：PreparePressBatch（失败→NotReady）→ CanEnterNonZeroPressure（失败→NotReady）
      ③ RunFactoryAlignedPress(kV×1000, reps=1, maxRangeRetries)
      ④ HardSafetyAbort / 安全出口 → _requiresReopen；消息附 SessionFault.FormatLatchSummary()
      ⑤ Kind：NotReady / OperatorStop / EStop / Safety / PicoOnly / DeviceOffline / CleanupGate
```

PressureRuntime.exe 仅用于 BenchSettings XML / configuration 反射；会话后不调用 Capture*/Monitor*。九宫格 UI 与加压页布局未改。

## 本轮差异与修复

| 差异 | 金标准（PressRound / 出厂） | 纠偏前 | 修复 |
|------|---------------------------|--------|------|
| 门禁顺序 | `PreparePressBatch` → `CanEnterNonZeroPressure` | 先 CanEnter，再 Prepare 且忽略返回值 | 同序；Prepare 失败→NotReady |
| 最高电压 | `targetKv > maximumKv` → NotReady | 未存/未检 maxKv | Open 存 `_maximumKv`；RunRound 门禁 |
| 安全退出 | `RequiresReopen` + `SessionFault.FormatLatchSummary` | 无 reopen；Safety 无锁存摘要 | `_requiresReopen` + 摘要 |
| Runtime / 第二路 DI | 无 | 曾 AttachDesktop / MonitorAsync 回退 | 已禁（前序） |
| 台架增益/过压/斜坡/pC/V | Open 时 Apply | 曾只传序列号 | 八参 Open（前序） |

## 有意不改

| 项 | 说明 |
|----|------|
| 加压测试 GUI | 布局、控件、交互不变 |
| R// / Imax / C=∫… | 出厂桌面指标；不接入 Xpert |
| EStop 宿主 DI 会话 | ModuleHost 保留同一会话做急停显示；桥接拒绝自动续压 |

## 门禁符号

`DeviceSdkLease` · `LiveEmergencyMonitor` · `SessionFault` · `StatusCopy` · `PreparePressBatch` · `RunFactoryAlignedPress` · `Task6RangeAdvisor` · `FactoryBenchHardOverCurrentA`

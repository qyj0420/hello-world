# 加压调用栈对照 — Xpert 实际路径 vs 出厂四项目

构建戳见 `NOTES_DAC_REBUILD.md`。权威源：`dac-FactoryPress-full-src-for-Codex_20261012-201121`（四项目整目录，与 `xpert/factory-press/` 逐文件一致）。

## 工厂路径（HvPressTest / OwtsServices）

```
PressTestRunner / FactoryPressApi
  → OwtsServices.CreateLive|CreateFakeLive
  → Acquisition.Open  （SingletonAcquisitionSession + LiveAcquisitionSession）
      · DeviceSdkLease（Global\Xiongxin.Owts.DeviceSdk.v1）
      · LiveEmergencyMonitor（P0.4 独立循环）
      · SessionFault 锁存
  → TryApplyBenchCalibration（增益/分压，禁占位 1000）
  → RampStepAoVolts ≈ clamp(60/gain, 0.002, 0.1)
  → AbsoluteOverVoltageLimitV = 最高电压×(1+trip)
  → PreparePressBatch（清软锁存）
  → RunFactoryAlignedPress(targetV, reps, …, Task6RangeAdvisor 量程记忆)
  → StatusCopy 五态 / CanEnterNonZeroPressure
```

## Xpert「加压测试」当前实际路径（纠偏后）

```
PressureTestControl.ConfigurePressureHardwareBackend
  → ModuleHost.ConfigureTask39PressureCoreAsync
      · PressureRuntime.exe 仅反射 BenchSettings / CreateConfigurationWithoutResidual（构造 settings XML）
      · 禁止 AttachDesktop（不再挂 Runtime FormClosing→FactoryResidentDevices）
      · EnsureFactoryPressSession
          → modules/FactoryPress/Xiongxin.Owts.XpertPressBridge.dll
          → OpenForXpert(art, pico, fake,
                PressureRatio, DividerRatio, MaximumVoltageKv,
                RangeIndex, CoefficientPcPerV)
              · OwtsServices.CreateLive|CreateFakeLive
              · Acquisition.Open（DeviceSdkLease / LiveEmergencyMonitor / SessionFault）
              · TryApplyBenchCalibration(Xpert 设备参数)     ← 本轮补齐的缺口
              · FactoryRampStepAoVolts / AbsoluteOverVoltageLimitV
              · TryValidateDualIdentity / ApplyXpertPdCalibrationPcPerVolt
              · MarkDischargeCalibrated
      · 急停监视：PollEmergencyStage（≈50ms）；禁止 MonitorAsync 第二路 DI
  → 每轮 RunFactoryPressRoundAsync
      → RunRoundAsync → PreparePressBatch → RunFactoryAlignedPress(kV×1000, reps=1, maxRangeRetries)
      → 故障 Kind：NotReady / OperatorStop / EStop / Safety / PicoOnly / DeviceOffline / CleanupGate
  → 成功写出 settings.xml + capture.bin + factory-press-manifest.json（rangeIndexUsed / locateSamplesPc）
```

## 曾存在的差异（已修）

| 差异 | 工厂 | 纠偏前 Xpert | 纠偏后 |
|------|------|-------------|--------|
| 台架增益/分压 | CreateBenchSample(PressureRatio, Divider) + TryApply | Open 只传序列号，未 Apply Xpert 比例 → 实机常「台架增益未 Apply」或错增益 | 与 XpertFactoryPressBackend.Open 同参 Apply |
| 过压上限 | maximumKv×1000×(1+trip) | 未设 | 已设 |
| 斜坡 AO 步长 | ≈60 V/步 | 默认 0.05 AO（高增益越窗） | FactoryRampStepAoVolts |
| 放电校准 pC/V | 校准结果写入 | 默认 2000，忽略 Xpert 匹配校准 | ApplyXpertPdCalibrationPcPerVolt |
| Runtime AttachDesktop | 无 | FormClosing→Resident CloseSession | 已移除 |
| MonitorAsync 第二路 DI | 无 | 会话空时回退 | 拒绝回退，只 PollEmergencyStage |

## 有意保留（非冒充硬件）

| 项 | 说明 |
|----|------|
| PressureRuntime.exe | 仅 BenchSettings XML / configuration 字符串 / ReviewCapturedRound（量程复核读盘），会话后不调用 Capture*/Monitor* |
| 九宫格计划表批次 | `FactoryPressureBatchContext` 仍驱动「任务编程」UI；每一轮硬件仍走 `RunFactoryAlignedPress(reps=1)`（与工厂 press-list 多轮等价：轮次在宿主循环） |
| HvPressTest UI | 参照，不替换九宫格 |

## 门禁符号核对（四项目）

- `DeviceSdkLease` — Acquisition/Live
- `LiveEmergencyMonitor` — Acquisition/Live（P0.4）
- `SessionFault` — Acquisition/Live + OwtsServices 查询
- `StatusCopy` — Contracts/Ui
- `RunFactoryAlignedPress` — HostAdapters/OwtsServices
- `Task6RangeAdvisor` — Acquisition 根目录

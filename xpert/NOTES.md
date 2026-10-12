# 加压测试 × 出厂加压机制

基线是上传的 HV-DAC Xpert 源码树（`SNAPSHOT_BINDING.json`：Schema `HVX.R58.AnalysisHotfixSourceSnapshot/1`，Version `1.1.2.0`，Revision `r58AnalysisHotfix`）。产物按 **Xpert V1.1.2 + factory-press 机制合并** 命名，不改到 1.1.4，也不换成 DesktopUi。

本代理只接线出厂加压的调用和故障路径进「加压测试」。九宫格其它页面的外观和文案留给并行修改。

硬件与错误处理只来自完整四项工程，目录在 `factory-press/`：

- `Xiongxin.Owts.Acquisition/`（Live、`Task6RangeAdvisor.cs`、SessionFault、DeviceSdkLease、ArtFreshFeedbackWindow、LiveEmergencyMonitor、PicoCaptureCooldown）
- `Xiongxin.Owts.Contracts/`（`IAcquisitionSession`、AcquisitionDtos、`StatusCopy`）
- `Xiongxin.Owts.HvPressTest/`（PressTestRunner / Form / Api，作参照，不替换九宫格）
- `Xiongxin.Owts.HostAdapters/`（`OwtsServices.RunFactoryAlignedPress`）

未纳入 `DesktopUi`。`Persistence`、`Algorithms`、`Ai` 只有编译垫片，加压路径不依赖它们的行为。

## 调用链

1. `src/ModuleHost.FactoryPressure.cs` 在配置加压页时加载 `Xiongxin.Owts.XpertPressBridge.dll`，调用  
   `OpenForXpert(art, pico, fake, PressureRatio, DividerRatio, MaximumVoltageKv, RangeIndex, PdCalibrationPcPerV)`。  
   实机走 `OwtsServices.CreateLive`；`OWTS_XPERT_FAKE_PRESS=1` 才走 FakeLive。Open 后 **TryApplyBenchCalibration**（Xpert 加压比例/分压，禁占位 1000）、`RampStepAo≈60V/步`、`AbsoluteOverVoltageLimitV`、放电校准 pC/V。Pico 按序列号单 Open，抑制 PicoScope.exe；连接灯 P0.5 置位。详细对照见 `NOTES_PRESSURE_CALLSTACK.md`。
2. 同一会话贯穿 0 kV 噪声初始化和非零加压。初始化交接不再 `CloseSession`，避免 Pico/ART 二次 Open。
3. 每一轮调用 `RunRoundAsync` → `OwtsServices.RunFactoryAlignedPress`（目标 kV×1000、重复 1 次、自动量程最多 2 次下调、手动至少 1 次）。停止按钮取消的令牌会 `RequestOperatorAbort`。
4. 急停显示不再调用旧 `UnifiedPressureBridge.MonitorAsync`（那会再开一路 DI）。界面约 50 ms 读 `PollEmergencyStage`；P0.4 的约 10 ms 循环仍在出厂 `LiveEmergencyMonitor` 里。
5. 成功时桥接写出工作台认识的 `settings.xml`、`capture.bin`（`XIONGXIN-ZERO-BENCH-2`）和 `factory-press-manifest.json`。`settings.xml` 的 `TargetVoltageV` 仍为 0，满足原校验；真实目标是单独参数。
6. 旧 `Xiongxin.Pressure.Runtime.exe` 仍用来反射构造 `BenchSettings` XML。会话打开后不再调用它的 `Capture*` / `Monitor*`。

找不到桥接 DLL 时直接失败，文案要求先运行合并脚本。不会悄悄退回旧采集进程。

## 故障种类

`XpertPressFaultException.Kind` 由 `FactoryPressFaultKind` 识别。

| Kind | 出厂来源 | 加压页行为 |
| --- | --- | --- |
| NotReady | `OperationalStatusBoard.CanEnterNonZeroPressure` 拒绝。已连接 ∧ 已校准 ∧ 非校准中 ∧ 急停解除 ∧ 未锁存 ∧ 目标 kV>0 才可加压。0 kV 不是可加压。校准则要求急停按下。 | 释放本轮预留，报告请求被拒绝，保留会话，不自动续轮。 |
| OperatorStop | 令牌取消或 `OperatorStopped`。文案 `StatusCopy.OperatorAbortNoLatch`。 | 停止本轮，完整冷却，不锁存，不自动续轮。 |
| EStop | `AcquisitionExitReason.EmergencyStop`。文案 `PressAbortHvOffPicoCapture`：HV OFF，P0.0/P0.3 同步断开，并尝试 Pico 同步触发采集。 | 急停停轮回执，撤销批次，保留同一 DI 会话。 |
| Safety | 过流、过压或其它未成功且非 Pico/掉线。同样走 HV OFF。 | 与急停相同的停轮回执。 |
| PicoOnly | 仅 Pico 掉线。出厂路径 `ClosePicoOnly` + 重开。文案 `PicoCommAnomalyRetry`。加压控制继续完成时，成功结果把该句写进状态行和清单 `picoCommWarning`。整轮未成功时抛出本种类。 | 不拆除会话，不要求重连。状态行提示通讯异常并建议重新加压。 |
| DeviceOffline | Open 失败、无结果，或文案含 `PressAbortDeviceOffline` / 整机掉线。整机掉线时跳过 Pico 同步采集，仍尽力关源。 | 结束会话，状态 `WorkerSessionExited`，必须重新连接。 |
| CleanupGate | `SingletonAcquisitionSession.ProcessCleanupFailed`。只有真正的 ClearTask/CloseUnit 失败才置位。 | 状态 `SessionEndedAfterFailedRelease`，禁止再次加压，直到确认源关断后重新连接。 |

五态文案来自 `StatusCopy`：已连接、已校准、可加压、急停按下、急停解除。`FormatFiveStates` 在不可加压时使用「不可加压」，避免把连接状态写成可加压。P0.4：物理高电平 = 急停解除，低电平 = 急停按下。

## 量程和定位进料

E+ 公式未改。改的是送进去的数。

- 量程：`RunFactoryAlignedPress` 记住成功量程，拒绝在上次成功之下盲目下调，单次下降不超过 2 档。清单字段 `rangeIndexUsed` 写回 `settings.xml` 的 `RangeIndex`。`PressureZeroVoltageCapture.ApplyFactoryPressRangeFeed` 让内存中的请求量程与记录一致，原字段逐项比对才通过。
- 定位：`FactoryLocateExtract` 做峰保持抽取，目标采样率 80 kHz，下限 20 kHz；若 `PdCalibrationPcPerVolt` > 0，样本为 pC。`PressureFactoryPressFeed` 读取 `locateSamplesPc`，再走现有 `PdLinearHighPass`，然后进入原 `SaveLocationCycleDataAsync`。没有清单或采样率低于 20 kHz 时，仍用页面上的电荷波形和 Pico 速率。

## 本机怎么得到可双击的程序

云端没有仓库自带的 `tools/compiler/csc.exe`，也没有 ART/Pico 驱动，不能在这里生成 `Xiongxin.OWTS.Desktop.exe`。Debug 是未加壳预览；Release 必须带完整保护参数。

先编 net48 桥接（需要 .NET SDK 8），再编桌面：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-FactoryPressMerge.ps1 -Configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Configuration Debug
```

受保护 Release：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-FactoryPressMerge.ps1 -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Configuration Release -Hybrid -ProtectedRelease -MaintenancePassword -V30BatchValidation
```

`Build.ps1` 把 `factory-press/dist/net48` 复制到 `build\<Configuration>\modules\FactoryPress`，并复制 DLL 到 exe 旁边。宿主按这个顺序查找桥接：`modules/FactoryPress`、程序目录、`factory-press/dist/net48`、桥接工程的 `bin/*/net48`。

依赖：.NET Framework 4.8 x64、.NET SDK 8（只用于编桥接）、仓库里的 `csc.exe`、`modules/PressureRuntime/Xiongxin.Pressure.Runtime.exe`（只构造设置 XML）、现场 ART 与 Pico 5242D 驱动。无驱动联调可设 `OWTS_XPERT_FAKE_PRESS=1`。

Linux 上只编桥接：

```bash
bash tools/build-factory-press-merge.sh Debug
```

输出目录：`deliverables/HV-DAC_Xpert_V1.1.2_factory-press-merge_Debug/`。FakeLive 冒烟工程是 `factory-press/Xiongxin.Owts.XpertPressBridge.Smoke`（net8.0）。它检查 0 kV 不是「可加压」、急停按下拒绝 2 kV、急停解除后为可加压，并写出 capture.bin / settings.xml / 清单。

## 频谱 KeyNotFound

对端「编写DAC软件」已停加压合并，补丁在 `uploads` 的 `HV-DAC_Xpert_V1.1.2_r58_SpectrumFix_*.tar.gz`。说明原文见仓库根目录 `MERGE_NOTES.md`。本树在加压合并之外吸收了这一处，避免整壳打包时再出现「操作未完成」。

`UnifiedSpectrumControl.State` 原先是 `states[mode]`。`states.Clear()` 之后，或刚切换 mode、`Rebuild` 尚未写入字典时，Interaction / Rendering / Camera 等 partial 读 `State` 会抛 `KeyNotFoundException`。宿主 `MainForm.ShowModuleError` 把模块异常显示成「操作未完成。请检查项目文件后重试；如仍失败，请联系维护人员。」

Getter 现与 `Rebuild` 相同：`TryGetValue` 失败则 `new ViewState()` 再 `Add`，然后返回。九宫格、`SpectrumLinkedSelection` 和 `CompetitionLinkedSpectra` 未改。DataAnalysis 源码随 `Build.ps1` / `Stage-Modules.ps1` 编进模块，不另打一份不含加压的 `*_HWFix_*` 包。


## 本分支相对 FactoryPressMerge 的 UI 加固

- `SetFactoryHardwareStatusLine`：状态文案变化时只改连接标签，不再整页 `UpdatePressureUi`（避免 LIVE 计划/轮次区闪烁）。
- `RefreshPlanGrid`：内容指纹未变则跳过 `Rows.Clear`。
- 反射阈值拖动：拖动中不同步 `NumericUpDown`；抬起后再 `SetAnalysisThreshold`，打断 ValueChanged→SetManualThresholdGuide 回环。

发布路径以 `modules/FactoryPress/Xiongxin.Owts.XpertPressBridge.dll` 为准（见 `build-sdk/build-linux.sh` / `Build.ps1`）。已删除 `FactoryPressOwtsHost` 模态路径。

## 改过的路径

- `modules-src/DataAnalysis/vendor/Spectrum/UnifiedSpectrumControl.cs` — `State` 缺失时惰性创建 `ViewState`，避免 `KeyNotFoundException` 被显示成「操作未完成」。
- `MERGE_NOTES.md` — 对端频谱补丁说明原文。
- `src/ModuleHost.FactoryPressure.cs` — 打开唯一会话、每轮 `RunRoundAsync`、故障种类、急停轮询、关闭、初始化不二次 Open。
- `modules-src/PressureTest/src/PressureFactoryPressFeed.cs` — 定位清单进料；状态行 `SetFactoryHardwareStatusLine`。
- `modules-src/PressureTest/src/PressureZeroVoltageCapture.cs` — `rangeIndexUsed` 写回请求量程。
- `modules-src/PressureTest/src/PressureRoundRecordSaving.cs` — 定位保存改用清单样本、有效采样率和现有高通。
- `modules-src/PressureTest/src/PressureTestLayout.cs` — 连接状态优先显示五态文案。控件布局未改。
- `factory-press/Xiongxin.Owts.Acquisition/` — 完整出厂采集工程。
- `factory-press/Xiongxin.Owts.Contracts/` — 完整契约，含 `StatusCopy`。
- `factory-press/Xiongxin.Owts.HvPressTest/` — 完整加压参照工程。
- `factory-press/Xiongxin.Owts.HostAdapters/` — `RunFactoryAlignedPress` / `FactoryLocateExtract`。
- `factory-press/Xiongxin.Owts.Persistence/CompileShims.cs` 与 `.csproj` — 编译垫片。
- `factory-press/Xiongxin.Owts.Algorithms/CompileShims.cs` 与 `.csproj` — 编译垫片。
- `factory-press/Xiongxin.Owts.Ai/CompileShims.cs` 与 `.csproj` — 编译垫片。
- `factory-press/Xiongxin.Owts.XpertPressBridge/` — Xpert 调用边界，不改出厂加压逻辑。
- `factory-press/Xiongxin.Owts.XpertPressBridge.Smoke/` — FakeLive 冒烟。
- `factory-press/Directory.Build.props`、`README_FOR_CODEX.md`、`PATHS_给Codex.txt`、`Xiongxin.Owts.pack-reference.sln` 及包内 docs / vendor-handoff。
- `Build.ps1` — 编桌面前要求并复制 net48 桥接。
- `tools/Build-FactoryPressMerge.ps1`、`tools/build-factory-press-merge.sh` — 生成 net48 桥接。
- `README.md`、`README.txt` — 指向本文。
- `.gitignore` — 忽略 `bin/`、`obj/`、`build/`、`uploads/`、`deliverables/`、`factory-press/dist/`。

# 雄新电力 · 雄新 OWTS — HV-DAC Xpert 桌面（FactoryPressMerge）运行说明

包名：`xpert-desktop-win-x64.zip` → 解压得到 `Xiongxin.OWTS.Desktop/`
主程序：`Xiongxin.OWTS.Desktop.exe`（九宫格工作台，WinForms WinExe，双击启动，无控制台黑窗）
构建戳、配置与逐文件 SHA256：见同目录 `BUILD_MANIFEST.json`

> 本包为 **SDK 构建的测试包**（非 ProtectedRelease），尚未经过实机验收。缺项见文末，请勿作为客户正式版发布。

权威对照：本机 `HV-DAC_Xpert_V1.1.2_FactoryPressMerge_Debug\Xiongxin.OWTS.Desktop.exe`（cloud `bc-5330ba65` / FactoryPressMerge 源码包）。加压路径与之对齐，不走旧 `FactoryPressOwts` 模态对话框。

## 1. 运行环境

- Windows 10 / 11 **x64**，已安装 **.NET Framework 4.8**（Win10 1903+ / Win11 自带）。
- 解压到 **本地可写目录**（如 `D:\XiongxinOWTS\`），不要直接在 zip 内双击运行。
- 用户配置写入 `%LOCALAPPDATA%\XIONGXIN\HVXUnifiedDesktop\DAC-Rebuild-FactoryPress-20261012\`，与已安装的 V1.1.2 正式配置隔离，互不覆盖。

## 2. 启动

双击 `Xiongxin.OWTS.Desktop.exe`。九宫格布局与导航与 Xpert V1.1.2 r58 保持一致。

## 3. 「加压测试」→ XpertPressBridge（唯一硬件入口）

加压页由宿主 `ModuleHost.FactoryPressure` 加载：

`modules\FactoryPress\Xiongxin.Owts.XpertPressBridge.dll`

| 调用 | 作用 |
|------|------|
| `OpenForXpert` | 同一会话贯穿 0 kV 噪声初始化与非零加压；Pico 单 Open，不启 PicoScope.exe |
| `RunRoundAsync` | → `OwtsServices.RunFactoryAlignedPress`（采集/故障走桥接） |
| `PollEmergencyStage` | 急停显示约 50 ms 回读；**不**再开 `UnifiedPressureBridge.MonitorAsync` 第二路 DI |
| `StatusCopy` 五态 | 已连接 / 已校准 / 可加压 / 急停按下·解除；0 kV ≠ 可加压；P0.0+灯同步；P0.4 独立循环 |

故障种类：`NotReady` / `OperatorStop` / `EStop`（HV OFF+Pico 同步）/ `Safety` / `PicoOnly`（ClosePicoOnly+重开，加压控制继续）/ `DeviceOffline` / `CleanupGate`（仅 ClearTask/CloseUnit 真失败）。

旧 `Xiongxin.Pressure.Runtime.exe` **仅**用于反射构造 `BenchSettings` XML。会话打开后 **不会** 调用其 `Capture*` / `Monitor*`。

找不到桥接 DLL 时加压 **直接失败**（文案要求先运行 `tools\Build-FactoryPressMerge.ps1`），不会静默退回旧采集进程。

操作流程：

1. 在 Xpert 中 **连接设备**、完成 **设备参数** 与 **校准**。
2. 进入「加压测试」并开始 — **无**出厂加压模态对话框；页面内直接走桥接轮次。
3. 停止按钮取消令牌 → `RequestOperatorAbort`；急停走桥接 `PollEmergencyStage` + 出厂 `LiveEmergencyMonitor`（P0.4）。

无驱动联调：环境变量 `OWTS_XPERT_FAKE_PRESS=1`。

### 实机所需（本包不含）

- ART 驱动 `Art_DAQ.dll`、PicoScope 5000A SDK `ps5000a.dll` / `picoipp.dll`。
- Xpert 设备连接依赖 `HvxAcquisitionNative.dll` 与 HardwareIntegration（见缺项）。

## 4. 已知缺项（本包）

1. **非 ProtectedRelease**：算法未经 ConfuserEx 加固（Linux SDK 构建）；正式版须在授权 Windows 环境用 `Build.ps1 -ProtectedRelease` 构建。
2. **未包含 `HvxAcquisitionNative.dll`（MSVC 原生桥）与 HardwareIntegration 连接器 / Task38**：本包无法完成 Xpert 实机连接；离线功能（工程、浏览、分析、报告）不受影响。
3. 未包含 Task17 定位 DLL、r71 研究运行时、缺陷模型 / ONNX、Hybrid 原生核心、设备资产目录、参考仿真资产。
4. 未做 Windows 实机验收；Linux 上完成桥接编译、桌面 SDK 构建与 FakeLive 冒烟。

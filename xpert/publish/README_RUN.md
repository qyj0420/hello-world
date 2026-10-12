# 雄新电力 · 雄新 OWTS — HV-DAC Xpert 桌面（合入出厂加压）运行说明

包名：`xpert-desktop-win-x64.zip` → 解压得到 `Xiongxin.OWTS.Desktop/`
主程序：`Xiongxin.OWTS.Desktop.exe`（九宫格工作台，WinForms WinExe，双击启动，无控制台黑窗）
构建戳、配置与逐文件 SHA256：见同目录 `BUILD_MANIFEST.json`

> 本包为 **SDK 构建的测试包**（非 ProtectedRelease），尚未经过实机验收。缺项见文末，请勿作为客户正式版发布。

## 1. 运行环境

- Windows 10 / 11 **x64**，已安装 **.NET Framework 4.8**（Win10 1903+ / Win11 自带）。
- 解压到 **本地可写目录**（如 `D:\XiongxinOWTS\`），不要直接在 zip 内双击运行。
- 用户配置写入 `%LOCALAPPDATA%\XIONGXIN\HVXUnifiedDesktop\DAC-Rebuild-FactoryPress-20261012\`，与已安装的 V1.1.2 正式配置隔离，互不覆盖。

## 2. 启动

双击 `Xiongxin.OWTS.Desktop.exe`。九宫格布局与导航与 Xpert V1.1.2 r58 保持一致（工程创建、工程管理、数据浏览、数据分析、报告生成、校准测试、加压测试等）。

## 3. 「加压测试」走出厂加压硬件栈

包根目录的 `factory-press-backend.enabled` 标记文件让「加压测试」页改走出厂加压硬件栈（位于 `modules\FactoryPressOwts\`）：

| 机制 | 作用 |
|------|------|
| `DeviceSdkLease` | Pico 5242D / ART 全局单 Open（`Global\Xiongxin.Owts.DeviceSdk.v1`） |
| `LiveEmergencyMonitor` | ART DB37 **P0.4 急停独立循环**监视；急停/异常 → HV OFF（P0.0 HVON 与 P0.3 高压灯同步断开）+ Pico 同步 |
| `SessionFault` | 安全退出后锁存：必须断开并重新连接设备，不自动续压；清理失败禁止同进程重开 |
| `StatusCopy` | 五态提示：已连接 / 已校准 / 可加压 / 急停按下·解除；0 kV ≠ 可加压 |
| `RunFactoryAlignedPress` + `Task6RangeAdvisor` | 出厂对齐加压流程与自动量程重加 |

操作流程：

1. 先在 Xpert 中 **连接设备**、完成 **设备参数**（加压比例、分压比、最高电压）与 **校准**。设备未连接时页面处于仿真模式，不会进入出厂加压栈。
2. 进入「加压测试」并开始 → 弹出「雄新 OWTS 出厂加压」对话框。
3. 依次点 **连接设备** → 设定目标 kV 与轮数 → **开始加压**（需二次确认）→ 需要时 **停止** → **断开设备**。
4. 每轮记录写到 `<工程>\Acquisition\PressureOwtsFactory\`（`owts-factory-round.json`、Pico 原始 ADC、分压原始电压）。

切回旧版 PressureRuntime 路径：删除 `factory-press-backend.enabled`，或设置环境变量 `XIONGXIN_OWTS_FACTORY_ALIGNED=0`。
标记存在但 `modules\FactoryPressOwts\` 缺失时，加压页 **拒绝运行**（不会静默换用另一套加压栈）。

### 实机所需（本包不含）

- ART 驱动 `Art_DAQ.dll`、PicoScope 5000A SDK `ps5000a.dll` / `picoipp.dll`（按厂商安装程序安装，或用 `OWTS_ART_DRIVER_DIR` / `OWTS_PICO_SDK_DIR` 指定目录）。
- ART 模块名默认 `Dev1`，可用 `OWTS_ART_DEVICE` 修改；Pico 按 Xpert 设备身份中的序列号打开。
- 台架标定 JSON：可用 `OWTS_BENCH_CALIBRATION_FILE` 指定；未指定时使用 Xpert 设备参数中的加压比例 / 分压比生成台架标定。加压比例为占位值 1000 时拒绝加压。
- Xpert 的设备连接本身依赖 `HvxAcquisitionNative.dll` 与 HardwareIntegration 连接器（见下方缺项）。

### 无驱动联调（FakeLive）

设置 `XIONGXIN_OWTS_FACTORY_ALIGNED_FAKE=1` 后，对话框使用仿真 ART/Pico（不碰真驱动），并显示「FakeLive 急停按下」勾选框用于验证急停锁存。仍需 Xpert 处于设备已连接状态才能进入该页。

## 4. 已知缺项（本包）

1. **非 ProtectedRelease**：算法未经 ConfuserEx 加固（Linux SDK 构建）；正式版须在授权 Windows 环境用 `Build.ps1 -ProtectedRelease` 构建。
2. **未包含 `HvxAcquisitionNative.dll`（MSVC 原生桥）与 HardwareIntegration 连接器 / Task38**：快照中缺厂商 Sxuptp / SxConnectDev 与清单，因此本包 **无法完成 Xpert 实机连接**，也就无法在实机上进入出厂加压对话框；旧 PressureRuntime / Task39 / 校准采集的硬件 I/O 同样受限。离线功能（工程、浏览、分析、报告）不受影响。
3. 未包含 Task17 定位 DLL、r71 研究运行时、缺陷模型 / ONNX、Hybrid 原生核心、设备资产目录、参考仿真资产。
4. 出厂加压轮次记录 **未接入** 页面分析与定位 E+（两者要求 ART 80 kS/s 与 Pico 同时基计数、禁止重采样，无法如实转换）；定位 E+ 数学未改动。
5. 未做 Windows 实机或桌面冒烟；只在 Linux 上完成编译、FakeLive 冒烟和接口契约检查。

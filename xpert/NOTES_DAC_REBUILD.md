# DAC 重编 NOTES — HV-DAC Xpert 整壳合入出厂加压

品牌：雄新电力 / 雄新 OWTS
构建戳：**2026-10-12T23:35:37+08:00**（Asia/Shanghai，`BUILD_MANIFEST.json` 中同值）

分析诊断 32 MiB 分段 + 定位三项目加载策略见 **`NOTES_DIAGNOSTICS_SEGMENTS.md`**。  
缺陷识别标准模型 / 拓展学习 / >80% 冲突重训见 **`NOTES_DEFECT_LEARNING.md`**。  
数据浏览/分析树 Ctrl 复选删除与减少无关刷新见 **`NOTES_TREE_MULTISELECT.md`**。

## 交付物

| 项 | 值 |
|----|----|
| 发布 zip | `xpert/publish/xpert-desktop-win-x64.zip`，**10,068,886 字节**，SHA256 `3AD7AA4ABD299EB035AE97A02BE80DA7DEBF9A1B5D3E3D5E21EBC7C637460BB3` |
| 主程序 | `Xiongxin.OWTS.Desktop/Xiongxin.OWTS.Desktop.exe`，.NET Framework 4.8 x64 WinExe（PE32+，GUI 子系统，无控制台），SHA256 `8B5FF380…` |
| 加压栈 | `modules/FactoryPressOwts/`：Bridge、HostAdapters.FactoryPress、Owts.Contracts、Owts.Acquisition（AnyCPU，仅引用 mscorlib/System/System.Core/WinForms/Drawing） |
| 包内容 | 30 个条目：主程序 + config、图标、国际化文档、七个 UI 模块、ReportEngine、CalibrationAcquisition、PressureRuntime、Task39、FactoryPressOwts、启用标记、README_RUN、BUILD_MANIFEST（逐文件 SHA256 与缺项清单） |

## 输入

| 来源 | 落点 | 核对 |
|------|------|------|
| Xpert 基线 `/tmp/xpert-base/` | `xpert/` | 与基线逐文件比对：仅下表所列文件有改动 |
| FactoryPress 全源 `dac-FactoryPress-full-src-for-Codex_20261012-201121` | `xpert/vendor/factory-press/`（Acquisition、Contracts、HostAdapters、HvPressTest 四项目整目录） | 与源逐字节一致（另加一个 `Xiongxin.Owts.FactoryPress.sln`） |
| Spectrum 修复 `HV-DAC_Xpert_V1.1.2_r58_SpectrumFix_20261012-2125` | `modules-src/DataAnalysis/vendor/Spectrum/UnifiedSpectrumControl.cs` | 与修复包逐字节一致，且 DataAnalysis 构建编译的就是该文件；说明在 `docs/spectrum-fix/` |
| 契约文档 `/tmp/contract/` | `docs/factory-press-contract/` | 一致 |

## 改动文件（相对 Xpert 基线）

| 路径 | 改动 |
|------|------|
| `src/FactoryPressOwtsHost.cs`（新） | 读取 `factory-press-backend.enabled` 标记 / `XIONGXIN_OWTS_FACTORY_ALIGNED`；从 `modules/FactoryPressOwts/` 反射加载 Bridge 并打开模态对话框；**请求了但 Bridge 缺失 → 抛错拒绝**，不静默换用旧栈 |
| `src/ModuleHost.FactoryPressure.cs` | `ConfigureTask39PressureCoreAsync` 在设备身份、校准选择通过之后，若请求 OWTS 则进入 `RunOwtsFactoryPressAsync`：重新校验校准，确认无其它加压会话持有设备，建立 `<工程>/Acquisition/PressureOwtsFactory/`，以 Xpert 设备参数（加压比例、分压比、最高电压、Pico 序列号、量程）调用对话框。此模式下 **不配置** 旧 Worker、DI 监视与页面采集后端，两套栈不会同时持有硬件。`owtsFactoryPressPage` 计入 `HasPressureDeviceOwner` / `HasForegroundPressureDeviceOwner`，退出、断开、切页的既有门禁照常生效 |
| `src/AssemblyInfo.cs` | `DAC_REBUILD_FACTORYPRESS_20261012` 下的 InformationalVersion 标注「SDK-built, not ProtectedRelease; physical acceptance pending」 |
| `src/AppContracts.cs` | 同一 define 下配置目录为 `DAC-Rebuild-FactoryPress-20261012`，不覆盖已安装 V1.1.2 的配置 |
| `Build.ps1` | 新增 `-DacRebuildFactoryPress`：Stage-Modules 后调用 `tools/Build-FactoryPressOwts.ps1`，并加 `/define:DAC_REBUILD_FACTORYPRESS_20261012` |
| `tools/Build-FactoryPressOwts.ps1`（新） | Windows：校验 press-only 源 → `dotnet build` Bridge（net48）→ 复制四个 DLL 到 `modules/FactoryPressOwts/` |
| `tools/extract-owts-factorypress.py`（新） | 从 vendor `OwtsServices.cs` 生成 press-only 的 `OwtsServices.FactoryPress.g.cs`（去掉工程库 / ONNX 依赖）；`--check` 核对源 SHA256 `116B8EBF…` |
| `tools/Build-GrokPressCompileOnly.ps1` | 默认源目录改为 `vendor/factory-press` |
| `build-sdk/`（新） | SDK 风格 csproj（net48 + `Microsoft.NETFramework.ReferenceAssemblies`），按 r58 配方编译主程序、七个 UI 模块、ReportEngine、CalibrationAcquisition、PressureRuntime、Task39；`build-sdk/factorypress/` 下为 HostAdapters.FactoryPress、**Bridge**（`XpertFactoryPressBackend` + `XpertFactoryPressDialog`）与 net8 冒烟；`build-linux.sh` 一键构建 + 打包 |
| 已删除 `src/OwtsFactoryAlignedHost.cs`、`tools/Stage-FactoryPressOwts.ps1`、`publish/xpert-dac-rebuild-source.zip` | 上一版在采集回调中直接调用 CompileOnly `FactoryPressApi`，绕过了校准选择、设备身份与单一持有者门禁，且默认 gain 回退 6000；旧源码 zip 内含该钩子 |

定位 E+ 数学、FactoryWorker 冻结源、九宫格布局与导航 **均未改动**。

## 出厂错误路径映射（OWTS 模式）

| 出厂加压机制 | Xpert 中的走向 | 结果 |
|--------------|----------------|------|
| `DeviceSdkLease`（`Global\Xiongxin.Owts.DeviceSdk.v1`） | 对话框「连接设备」→ `OwtsServices.Acquisition.Open` | Pico 5242D 单 Open；另一程序持有，或上次异常退出留下 AbandonedMutex → 拒绝打开、回滚 Close |
| 双身份校验 | Pico 期望序列号 = Xpert 设备身份中的 Pico 序列号；ART 按模块名（`OWTS_ART_DEVICE`，默认 `Dev1`）打开 | Pico 不符拒绝；ART 序列号仅记录（见缺项） |
| `StatusCopy` 五态门禁 | 每轮前 `PreparePressBatch` + `CanEnterNonZeroPressure` | 未校准 / 急停按下 / 未连接 → 按 StatusCopy 原文拒绝；0 kV 噪声轮允许 |
| 加压比例占位 1000 | `Open` 前拒绝 | 「台架加压比例未标定」 |
| `RunFactoryAlignedPress` + `Task6RangeAdvisor` | `PressRound` → `RunFactoryAlignedPress(kV×1000, 1, token, log, maxRangeRetries:2, 初始量程)` | 自动量程重加；斜坡步长 `clamp(60/gain, 0.002, 0.1)` AO V（≈60 V/步，与 PressTestRunner 一致，避免默认 0.05 AO V 在 gain 6000 下 300 V/步越过 ±5% 窗） |
| 过压上限 | `AbsoluteOverVoltageLimitV = 最高电压 × (1 + ClampOverVoltageTripFraction)` | 越限 → 安全退出 |
| 操作员停止 | 「停止」→ `RequestOperatorAbort` | `OperatorStopped`：关源，不锁存，P0.4 仍独立监视 |
| `LiveEmergencyMonitor`（P0.4 独立循环）/ 过流 / 过压等硬安全退出 | `PressRound` 结果 | `SafetyAbort`：HV OFF（P0.0 HVON 与 P0.3 灯同步断开）+ Pico 同步；`SessionFault` 锁存，**必须断开并重新连接**，不自动续压 |
| RampTimeout 等其它失败 | 同上 | `Failed`，停止后续轮次 |
| 关闭清理失败 | 「断开设备」/ 关窗 → `Close()` 返回失败文本 | 错误对话框，本进程禁止再次打开硬件 |
| 加压中关窗 | `FormClosing` | 取消关闭并请求停止 |
| P0.5 连接灯 | 随 Live 会话 Open/Close | vendor `LiveAcquisitionSession`：Open 后置高并维持，Close 置低 |

每轮落盘：`owts-factory-round.json`（outcome、量程、`analysisFeed: NotConnected`）、`pico-raw-adc.i16le`、`divider-raw-volts.f64le`；写盘失败会并入该轮的结果消息。

## 构建与验证结果（本 Linux 环境，未触碰任何设备）

| 检查 | 结果 |
|------|------|
| `build-sdk/build-linux.sh`（Release，14 个项目） | **全部成功，0 错误**；仅 PressureTest 有 2 条 CS0436（`ProjectMeasurementMode` 与 HVX.PdReview 同名类型，r58 原配方固有）；主程序与 Bridge 0 警告 |
| PE 头 | 全部 exe 为 x64 PE32+、GUI 子系统；UI DLL x64；FactoryPress DLL AnyCPU |
| 程序集引用 | 主程序与 FactoryPress DLL 仅引用 .NET Framework 4.8 自带程序集，无需额外 NuGet DLL |
| Bridge FakeLive 冒烟（`Xiongxin.OWTS.FactoryPressBridge.Smoke`） | **SMOKE OK**：未校准拒绝、急停按下拒绝、2 kV 轮 TargetReached 且落盘、0 kV 噪声轮、操作员停止不锁存、超最高电压拒绝、急停 → SafetyAbort + 需重连 + 不自动续压、Close 释放 Lease |
| 宿主契约检查（`--contract` 对已打包 Bridge） | **PASS**：`ShowForXpert(IWin32Window, string, string, double, double, double, int, string, bool, Action<string,string>) -> string` 与 `FactoryPressOwtsHost` 反射调用一致 |
| vendor HvPressTest `--smoke --gain 6000` | `OK exit=0 completed=2` |
| `extract-owts-factorypress.py --check` | OK（源 SHA256 `116B8EBF…` 一致） |

复现：

```bash
cd xpert
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
./build-sdk/build-linux.sh                    # 产出 publish/xpert-desktop-win-x64.zip
S=build-sdk/factorypress/Xiongxin.OWTS.FactoryPressBridge.Smoke
dotnet build $S -c Release
HOME=$(mktemp -d) dotnet $S/bin/Release/net8.0/Xiongxin.OWTS.FactoryPressBridge.Smoke.dll
dotnet $S/bin/Release/net8.0/Xiongxin.OWTS.FactoryPressBridge.Smoke.dll --contract \
  publish/Xiongxin.OWTS.Desktop/modules/FactoryPressOwts/Xiongxin.OWTS.FactoryPressBridge.dll
```

Windows 原生构建（未在本环境验证）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Configuration Release -DacRebuildFactoryPress
```

## 缺项 / 风险

1. **非 ProtectedRelease**：SDK 构建，未经 ConfuserEx / `Protect-OwnedAlgorithms.ps1`；InformationalVersion 与 manifest 均标注。正式版须在授权 Windows 环境用 `Build.ps1 -ProtectedRelease` 构建（PublicBeta-NoDriver 同理）。
2. **`HvxAcquisitionNative.dll` 未构建**（需 MSVC），**HardwareIntegration 连接器与 Task38 未构建**（快照缺厂商 Sxuptp / SxConnectDev 与清单）。后果：本包 **无法完成 Xpert 实机连接**；「加压测试」只有在设备已连接时才进入 OWTS 路径（未连接时为仿真模式，此门禁未放宽），所以实机上目前进不了出厂加压对话框；旧 PressureRuntime / Task39 / CalibrationAcquisition 的硬件 I/O 也受限。
3. 厂商驱动 `Art_DAQ.dll`、`ps5000a.dll`、`picoipp.dll` 不随包。
4. 未包含：Task17 定位 DLL（缺三件私有运行时 DLL）、r71 研究运行时、缺陷模型 / ONNX、Hybrid 原生核心、设备资产目录（隐私排除）、参考仿真资产。
5. OWTS 轮次 **未接入** 页面分析与定位 E+：旧 BenchCapture 格式要求 ART 80 kS/s 与 Pico 同时基计数且禁止重采样，出厂加压周期无法如实转换，因此只落原始数据并标 `NotConnected`。
6. OWTS 模式下 ART 序列号只记录不校验（OWTS 按模块名打开 ART）；IMON / VMON 比例沿用 FactoryPress 默认值；台架标定由 Xpert 加压比例 / 分压比生成，除非设置 `OWTS_BENCH_CALIBRATION_FILE`。
7. net48 依赖系统自带 .NET Framework 4.8，非自包含。
8. **未做实机验收，也未做 Windows 桌面冒烟**（本环境无 Windows / wine）；`Build.ps1 -DacRebuildFactoryPress` 原生路径未执行。

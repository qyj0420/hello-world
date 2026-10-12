# DAC 全面重编 NOTES — HV-DAC Xpert 合入出厂加压硬件栈

**品牌：** 雄新电力 / 雄新 OWTS（禁止「雄芯」）  
**时间戳：** 2026-10-12T21:55:00+08:00（Asia/Shanghai）  
**基线：** `HV-DAC_Xpert_from-user-20261012.zip` → `/workspace/xpert/`  
**加压源：** `dac-FactoryPress-full-src-for-Codex_20261012-201121.zip` → `vendor/factory-press/`（四项目整目录）  
**Spectrum：** `HV-DAC_Xpert_V1.1.2_r58_SpectrumFix_20261012-2125` → `modules-src/DataAnalysis/vendor/Spectrum/UnifiedSpectrumControl.cs`

## 本轮改动文件

| 路径 | 作用 |
|------|------|
| `vendor/factory-press/**` | 出厂加压四项目整目录（Acquisition 含根目录 `Task6RangeAdvisor.cs`、Contracts、HostAdapters、HvPressTest） |
| `modules-src/DataAnalysis/vendor/Spectrum/UnifiedSpectrumControl.cs` | State `TryGetValue` lazy 修复（KeyNotFound） |
| `src/OwtsFactoryAlignedHost.cs` | Desktop 加载 `Grok.Pressure.CompileOnly.dll` → `FactoryPressApi` |
| `src/ModuleHost.FactoryPressure.cs` | 「加压测试」捕获优先走 FactoryPressApi 完整硬件路径 |
| `tools/Build-GrokPressCompileOnly.ps1` | 默认 `ImportedSourceRoot` → `vendor/factory-press` |
| `tools/Stage-FactoryPressOwts.ps1` | 将 CompileOnly DLL 放到 `modules/FactoryPressOwts/` |
| `docs/factory-press-contract/**` | 契约对照文档 |
| `publish/README_RUN.md` | 客户运行 / Windows 构建说明 |

## 工厂错误路径 / 硬件机制映射

| 出厂加压（权威） | Xpert 合入点 | 说明 |
|------------------|--------------|------|
| `DeviceSdkLease` (`Global\Xiongxin.Owts.DeviceSdk.v1`) | `vendor/.../Live/DeviceSdkLease.cs` → `FactoryPressApi.Connect` → `LiveAcquisitionSession.Open` | Pico 5242D 全局单 Open；AbandonedMutex 拒绝打开 |
| `LiveEmergencyMonitor` / P0.4 独立循环 | `Live/LiveEmergencyMonitor.cs`（随 LiveAcquisitionSession） | 与 ART DI 急停独立；10ms 级轮询 |
| `SessionFault` | `Live/SessionFault.cs` + cleanupFailed 门闩 | 故障落盘；清理失败禁同进程重开 |
| `StatusCopy` | `Contracts/Ui/StatusCopy.cs` | 急停按下=校准/AO0；解除=非零加压；**0kV≠可加压** |
| `RunFactoryAlignedPress` / HostAdapters | `FactoryPressApi.Press` ≡ 同路径编排（CompileOnly 含 HvPressTest） | Desktop 经 `OwtsFactoryAlignedHost` 调用 |
| `Task6RangeAdvisor`（**Acquisition 根目录**） | `Acquisition/Task6RangeAdvisor.cs` | Press 内自动量程重加；勿只拷 Live/ |
| ART DB37 P0.0 HVON+加压灯 | `LiveAcquisitionSession` / `ArtPicoLiveBackend` | HVON↔P0.3 灯同步 |
| P0.3 高压灯 | 同上 | |
| P0.4 急停 | `LiveEmergencyMonitor` | 急停/异常 → HV OFF + Pico 同步 |
| P0.5 连接灯 | Live 硬件选项 / DO 映射 | |
| 仅 Pico 掉线 | Live 会话策略 | 完成加压控制并提示（整机掉线除外） |

### 与既有 FactoryWorker 并行类型（勿混淆）

| FactoryWorker（PressureRuntime） | Owts 出厂栈 | 合入策略 |
|----------------------------------|-------------|----------|
| `FactoryDeviceLease` (`…FactoryAcceptance.DeviceSdk.v1`) | `DeviceSdkLease` (`…Owts.DeviceSdk.v1`) | **加压捕获优先 Owts**；Worker 哈希冻结，未改 mutex 名以免破坏 provenance |
| `FactoryEmergencyMonitor` | `LiveEmergencyMonitor` | Owts 路径用后者 |
| `FactorySessionFault` | `SessionFault` | Owts 路径用后者 |
| `NativeFactoryBackend` | `ArtPicoLiveBackend` + `LiveAcquisitionSession` | Owts 路径用后者 |
| `FactoryPressureSequence` 量程 | `Task6RangeAdvisor` | Owts Press 内用 Advisor；Legacy Review 仍走 Worker 直到 DLL 就绪 |

## Windows 构建（本 Linux 箱无 `csc.exe` PE，不能产出 Desktop.exe）

```powershell
# Debug 预览（无 ProtectedRelease）
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Configuration Debug

# 出厂加压 CompileOnly + Stage
powershell -NoProfile -File .\tools\Build-GrokPressCompileOnly.ps1 `
  -OutputRoot (Join-Path $PWD 'build\factory-press-owts')
powershell -NoProfile -File .\tools\Stage-FactoryPressOwts.ps1 `
  -CompileOutputRoot (Join-Path $PWD 'build\factory-press-owts') `
  -DesktopBuildRoot (Join-Path $PWD 'build\Debug')

# 客户 ProtectedRelease（需维护口令与完整依赖）
# .\Build.ps1 -Configuration Release -Hybrid -V30BatchValidation -ProtectedRelease -MaintenancePassword
# PublicBeta-NoDriver：
# .\Build.ps1 -Configuration Release -Hybrid -V30BatchValidation -ProtectedRelease -MaintenancePassword -PublicBetaNoDriver
```

输出：`build\Release\Xiongxin.OWTS.Desktop.exe`（WinExe，无控制台黑窗）。

## 冒烟（本箱可跑）

`vendor/factory-press` FakeLive：

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
cd vendor/factory-press
dotnet build Xiongxin.Owts.HvPressTest/Xiongxin.Owts.HvPressTest.csproj -c Release -f net8.0
dotnet run --project Xiongxin.Owts.HvPressTest -c Release --framework net8.0 -- --smoke --gain 6000
# 期望 OK exit=0 completed=2
```

## 仍缺项

1. **本环境无法生成** `Xiongxin.OWTS.Desktop.exe`（缺 Windows `tools/compiler/csc.exe` 与 ProtectedRelease 依赖）。  
2. **ProtectedRelease / 加密模型** 须在授权 Windows 工控机构建；Debug 可预览。  
3. **Art_DAQ.dll / ps5000a.dll** 不随源码包。  
4. FactoryWorker 冻结哈希门禁下未改 `NativeFactoryBackend` 本体；实机完整 Owts 路径依赖 Stage 后的 CompileOnly DLL。  
5. 定位 E+ 数学未改；Owts 路径量程喂数由 `Task6RangeAdvisor` 负责，Legacy Review 仍用 Worker 序列直到 DLL 就位。

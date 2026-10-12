# MERGE_NOTES — HVX V1.1.2 r58AnalysisHotfix / FactoryPressMerge

**时间：** 2026-10-12 Asia/Shanghai（UTC+8）  
**品牌：** 雄新  
**基线：** SNAPSHOT_BINDING Version 1.1.2.0 / Revision r58AnalysisHotfix  
**权威加压对照：** 上传包 `HV-DAC_Xpert_V1.1.2_FactoryPressMerge_SpectrumFix_src`（云 `bc-5330ba65`）+ 完整四项目 `dac-FactoryPress-full-src-for-Codex_20261012-201121`

## 加压 / 硬件路径 ownership

| 工作项 | 负责人 | 说明 |
|--------|--------|------|
| FactoryPress 实机调用 + 错误/重试/急停/lease/cooldown | **本分支** `cursor/factorypress-exe-7ed9` | 以 FactoryPressMerge 为正解；**停止**沿用 hello-world 缺 HvxAcquisitionNative、非完整桥接的 `FactoryPressOwts` / `ShowForXpert` 模态合流 |
| Spectrum State KeyNotFound | 已合入 | `UnifiedSpectrumControl.State` lazy create（见 NOTES.md） |

硬件四项目整目录：`xpert/factory-press/`  
Acquisition（含根目录 Task6RangeAdvisor）、Contracts、HostAdapters、HvPressTest + `Xiongxin.Owts.XpertPressBridge`。

## 调用链（门禁）

1. `src/ModuleHost.FactoryPressure.cs` → 加载 `modules/FactoryPress/Xiongxin.Owts.XpertPressBridge.dll` → `OpenForXpert`
2. 同一会话贯穿 0 kV 与非零加压；初始化交接不二次 Open
3. 每轮 `RunRoundAsync` → `OwtsServices.RunFactoryAlignedPress`
4. 急停读 `PollEmergencyStage`（**勿** `UnifiedPressureBridge.MonitorAsync` 第二路 DI）
5. 采集/故障必须走桥接；**禁止**悄悄退回 `Xiongxin.Pressure.Runtime.exe` 的 Capture*/Monitor*（Runtime 仅构造 BenchSettings XML）
6. 故障映射：NotReady / OperatorStop / EStop / Safety / PicoOnly / DeviceOffline / CleanupGate
7. StatusCopy：急停按下=校准/AO0；解除=非零加压；0kV≠可加压；P0.0+灯同步；P0.4 独立循环；Pico 单 Open、禁 PicoScope.exe

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-FactoryPressMerge.ps1 -Configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Configuration Debug
```

Linux：`bash tools/build-factory-press-merge.sh` + `bash build-sdk/build-linux.sh`  
产出：`publish/xpert-desktop-win-x64.zip`，内含 `modules/FactoryPress/Xiongxin.Owts.XpertPressBridge.dll`。

## 已删除 / 不再作为正解

- `src/FactoryPressOwtsHost.cs`（ShowForXpert 模态）
- 发布路径 `modules/FactoryPressOwts/` 与「删标记回退 Runtime」叙事

# DAC 重编 NOTES — HV-DAC Xpert × FactoryPressMerge（权威纠偏）

品牌：雄新电力 / 雄新 OWTS  
构建戳：**2026-10-13T00:16:27+08:00**（Asia/Shanghai）  
权威对照：上传 `HV-DAC_Xpert_V1.1.2_FactoryPressMerge_SpectrumFix_src`（`bc-5330ba65`）+ 完整四项目 `dac-FactoryPress-full-src-for-Codex_20261012-201121`  
**停止**沿用 hello-world 里缺 `HvxAcquisitionNative`、非完整桥接的 `FactoryPressOwts` / `ShowForXpert` 合流当作正解。

加压机制全文见根目录 **`NOTES.md`**；ownership 见 **`MERGE_NOTES.md`**；调用栈对照见 **`NOTES_PRESSURE_CALLSTACK.md`**。  
多文件分析 spill 见 `NOTES_ANALYSIS_MEMORY.md`；诊断分段见 `NOTES_DIAGNOSTICS_SEGMENTS.md`；缺陷学习见 `NOTES_DEFECT_LEARNING.md`；树多选见 `NOTES_TREE_MULTISELECT.md`。

## 交付物

| 项 | 值 |
|----|----|
| 发布 zip | `xpert/publish/xpert-desktop-win-x64.zip`，**10,490,871 字节**，SHA256 `0914BB17D456AECE597483FF275B2812741863271F5286BF08BF4BDEC5841197` |
| 主程序 | `Xiongxin.OWTS.Desktop/Xiongxin.OWTS.Desktop.exe`，.NET Framework 4.8 x64 WinExe |
| 加压栈 | **`modules/FactoryPress/`**：`Xiongxin.Owts.XpertPressBridge.dll` + Acquisition/Contracts/HostAdapters 依赖 |
| 宿主入口 | `src/ModuleHost.FactoryPressure.cs`：`OpenForXpert` / `RunRoundAsync` / `PollEmergencyStage` |
| 已删除 | `src/FactoryPressOwtsHost.cs`（不再弹 OWTS 模态） |

## 调用链（必须）

1. 配置加压页 → 加载 `Xiongxin.Owts.XpertPressBridge.dll` → `OpenForXpert(…, PressureRatio, DividerRatio, MaximumKv, Range, PdCal)`（同一会话贯穿 0 kV 与非零加压；Apply 台架增益/过压/斜坡，与出厂 bot 一致）
2. 每轮 `RunRoundAsync`（对齐 `XpertFactoryPressBackend.PressRound`）：`targetKv≤maximumKv` → `PreparePressBatch` → `CanEnterNonZeroPressure` → `RunFactoryAlignedPress`；HardSafetyAbort → `_requiresReopen` + `SessionFault.FormatLatchSummary`
3. 急停显示读 `PollEmergencyStage`（**禁止**第二路 `MonitorAsync` / Runtime `AttachDesktop`）
4. `Xiongxin.Pressure.Runtime.exe` **仅**构造 BenchSettings XML；会话后不调用 Capture*/Monitor*
5. 找不到桥接 DLL → 失败关闭，不静默回退旧栈  
调用栈全文：`NOTES_PRESSURE_CALLSTACK.md`。加压页 GUI 不改。

故障：`NotReady` / `OperatorStop` / `EStop` / `Safety` / `PicoOnly` / `DeviceOffline` / `CleanupGate`（见 `NOTES.md` 表）。

## 构建

```bash
cd xpert
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
bash tools/build-factory-press-merge.sh Debug
CONFIG=Release bash build-sdk/build-linux.sh
```

Windows：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-FactoryPressMerge.ps1 -Configuration Debug
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Configuration Debug
```

`Build.ps1` / `build-linux.sh` 将 `factory-press/dist/net48` 复制到 `modules/FactoryPress`（及 exe 旁）。

FakeLive 冒烟：`factory-press/Xiongxin.Owts.XpertPressBridge.Smoke`（`OWTS_XPERT_FAKE_PRESS=1`）。

## UI 纠偏（本轮）

- 反射阈值拖动：按下冻结波形底图，移动只脏刷导引线；`SetAnalysisThreshold` 静默同步，无 ValueChanged 回环闪烁

- LIVE 状态行：`SetFactoryHardwareStatusLine` 只更新连接标签，避免整页 `UpdatePressureUi` 闪烁
- 计划表：`RefreshPlanGrid` 指纹相同则跳过 `Rows.Clear`
- 反射阈值拖动：拖动中不同步 NumericUpDown；鼠标抬起再提交，避免 ValueChanged 回环闪烁

# MERGE_NOTES — HVX V1.1.2 r58AnalysisHotfix / base-r55-fix

**时间：** 2026-10-12 Asia/Shanghai（UTC+8）  
**品牌：** 雄新  
**基线：** SNAPSHOT_BINDING Version 1.1.2.0 / Revision r58AnalysisHotfix  
**本聊天树：** `/workspace/base-r55-fix/`

## 加压 / 硬件路径 ownership（勿撞车）

| 工作项 | 负责人 | 说明 |
|--------|--------|------|
| FactoryPress 实机调用 + 错误/重试/急停/lease/cooldown | **对端** cloud `bc-5330ba65`（DAC全面重编） | 同基线 f6480caa；整包四项目并入 r55 Debug |
| 本树加压/Acquisition/HostAdapters/FactoryWorker 压力路径 | **本代理不做、未改** | 曾只读包文档；**未**拷贝覆盖、**未**改 FactoryWorker / e-stop / lease / AO |

硬件包路径（对端用，本树勿再整夹替换）：  
`/workspace/pressure-hw-err-pack/dac-FactoryPress-full-src-for-Codex_20261012-201121/`  
（HvPressTest / Acquisition(+Task6RangeAdvisor) / Contracts / HostAdapters）

## 本树已做（非加压）

1. **UnifiedSpectrumControl State KeyNotFound**  
   - 文件：`modules-src/DataAnalysis/vendor/Spectrum/UnifiedSpectrumControl.cs`  
   - 问题：`State` getter 直接 `states[mode]`；`states.Clear()` 后或切 mode 后、Rebuild 前，partials（Interaction/Rendering/Camera…）访问 → `KeyNotFoundException`  
   - 修复：与 `Rebuild` 相同策略，getter 内 `TryGetValue` 缺失则 `new ViewState()` 再 Add（lazy create before Rebuild）  
   - **未改** 九宫格 Desktop UI、双窗联动（`SpectrumLinkedSelection` / `CompetitionLinkedSpectra` 已存在，本轮不动）

2. **嵌入模块 / 发包**  
   - Spectrum 源在 DataAnalysis vendor，随 `Build.ps1` / `Stage-Modules.ps1` 编进 DataAnalysis 模块  
   - 要进可点包：Windows 上对**已含对端加压合并后的树**做 ProtectedRelease；**不要**用本树单独打 `*_HWFix_*`（加压未合入）  
   - 本树仅出 spectrum 修复源码包（可选 zip，见下）

## 路径约定（避免碰撞）

- 对端加压合并产物建议目录/标签：`*_r55_Debug_*` / cloud bc-5330ba65 自己的 out 树  
- 本聊天 spectrum-only 交付：`/workspace/HV-DAC_Xpert_V1.1.2_r58_SpectrumFix_*`（无 FactoryPress 覆盖）  
- 勿在两边同时整目录覆盖 `modules-src/FactoryWorker` 或引入第二套 `Xiongxin.Owts.*`

## 未做 / 缺口

- 无 Windows ProtectedRelease 可点包（加压归对端；本机未合硬件路径）  
- 无 FactoryPress / Acquisition / Contracts / HostAdapters 合并  
- 未发明硬件行为  

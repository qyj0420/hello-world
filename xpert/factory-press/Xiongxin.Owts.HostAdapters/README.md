# HostAdapters

组合根 `OwtsServices.CreateSimulation` 组装：

- `SingletonAcquisitionSession` → 内层 `SimulationAcquisitionSession`（不加载 Art/Pico；全局一次 Open）
- `OnnxDefectRecognizer` + `OnnxBackedDefectRecognitionService`（无 `OWTS_ONNX_MODEL` → `ModelMissing`）
- `IFirstZeroPhaseEvaluator`（无 `OWTS_TASK6_RUNTIME_DIR`/三件套 → `MissingDll`；见 `docs/TASK6_BRIDGE.md`）
- 阶段2 滤波/简化定位 + `NotImplementedConclusionEngine`
- `OperationalStatusBoard` + `StatusCopy` 五态
- `PlaceholderReportExporter`
- `SqliteProjectRepository` + `SqliteProjectWorkspace`

接 WinForms 真壳的约定见 `ModuleHostBridgeNotes.cs`。当前演示入口是 `Xiongxin.Owts.DesktopHost`。

## 本轮接入

- 采集：`OwtsServices.Acquisition` = `SingletonAcquisitionSession.CreateSimulation()`（全局一次 Open）
- 五态：`StatusBoard` / `FormatStatusFiveStates()`；常量见 `StatusCopy`
- 报告：`MinimalHtmlReportExporter`（HTML/MD + SVG；可选 `WaveformPngChart` PNG）
- P0.5：`SetConnectionIndicationLamp`（仿真 Console 日志）


## 工程库浏览（现场）

- `ProjectLibraryCatalog`：发现/最近列表/`Describe`/`ListAcquisitionSummaries`/`ListReportFiles`
- 约定路径：`project.owts.db`、`*.owts.db`、`*.owts`，或含 `ITEM.in`+`CABLE.in` 的目录
- 环境变量：`OWTS_PROJECT_LIBRARIES_ROOT`（扫描根）
- `OwtsServices.ListProjectLibraries` / `TrySwitchProjectLibrary` / `ListCurrentAcquisitionSummaries`
- 切换库不关闭采集会话；状态文案仍走 `StatusCopy` 五态


## 台架标定编辑（BenchCalibrationEditor）

- `TryValidateBeforeSave` / `TryParseJson` / `TryLoad` / `TrySave`：合法 JSON 往返；**拒绝** `benchCalibrated=true` 且 Gain=1000
- `OwtsServices.TryApplyBenchCalibration`：仅 Live/FakeLive 会话成功；仿真返回说明文案
- 文案：`StatusCopy.BenchGainNotCalibrated`（未台架标定禁止真高压）
- 样本：`fixtures/bench-calibration.sample.json`

## StatusJson 落盘（CALv5 复测）

- `DacStatusJsonCapture/v2`：dated bundle（JSON + meta + `cal_stub_header` + `RETEST_CHECKLIST`）
- `OwtsServices.DumpMidStatusJson(import, mid, outDir, thr=2, algos)`
- 一键：`tools/Run-StatusJsonDump-CalV5.ps1`；文档：`docs/ACCURACY_COMPARE.md` §3.16
- **禁止 thr1**；无 Worker 时仍写 fallback + 清单（box 可 Smoke）

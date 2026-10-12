# 分析诊断分段（32 MiB）— 格式、兼容与流畅度

构建戳：**2026-10-12T23:05:12+08:00**（与 `NOTES_DAC_REBUILD.md` / `BUILD_MANIFEST.json` 一致；本改动随 Desktop 包一并重编）。

## 问题

多文件大工程下，`AlgorithmDiagnostics` 以单段 JSON 写入 `session.json`，编码占用逼近约 **64 MiB** 时被 `SessionMetadataBytes` / 序列化配额拦住，分析无法保存。笼统「放宽 64MB」不能解决峰值内存与单文件膨胀。

## 格式约定

| 项 | 约定 |
|----|------|
| 触发阈值 | 编码后诊断字典 ≥ **32 MiB**（`SegmentTriggerBytes` / `EncodedDiagnosticsBytes`）即分段落盘 |
| 单段文件 | `diagnostics-part-001.json` … `diagnostics-part-064.json`（每段 ≤ 32 MiB UTF-8） |
| 段内格式 | 与旧版相同：`Dictionary<path, encodedStatus>` 的 JSON（可含 `HVX.Diagnostics.Gzip.v1:`） |
| 信封（session 内） | `AlgorithmDiagnostics` 值为小 JSON：`schema=HVX.AlgorithmDiagnostics.Segmented/1`，`version=1`，`parts[]`，`recordCount`，`encodedBytes` |
| 未达阈值 | 仍写**旧版单段扁平 JSON**（无 envelope），最大兼容 |
| 总硬顶 | 全部分段编码合计 / 解码工作集 ≤ **512 MiB**；最多 **64** 段 |
| 静态/测试往返 | 无存档目录时，envelope 可带 `inlineParts[]`（仅内存往返；命名存档写文件引用、不内联） |

常量：`modules-src/DataAnalysis/src/ReviewAnalysisMetadataBudget.cs`。

## 兼容策略

1. **读旧版**：无 `Segmented/1` schema 的字符串仍按扁平 map 解析；编码上限保持历史 **64 MiB**（`SessionMetadataBytes`），不破坏已有工程。
2. **读新版**：`LoadSavedAnalysis` / Competition 读档传入分析目录，按 `parts` 顺序读段并合并；段文件必须出现在 `analysis.json` 的 `Files[]` 且哈希校验。
3. **写新版**：`SaveNamedAnalysis` 在导出后取出 `pendingDiagnosticsArchiveParts`，写入段文件并列入 manifest；`session.json` 只保留小 envelope。
4. **回滚/内存**：`CaptureNamedAnalysisRollback` 在 `PrepareSession` 前清空 session 内诊断字段，并从内存字典恢复，避免误解析无段文件的 envelope。

## 进度条入口

- UI：`ReviewAnalysisProgressDialog.BeginReadingSavedAnalysis` / `UpdateReadingSavedAnalysis`
- 文案键：`Reading saved analysis` → 中文 **「读取旧有分析」**（`ReviewAnalysisProgressText`）
- 调用点：`ReviewAnalysisArchives.LoadSavedAnalysis` — 仅当 `AlgorithmDiagnostics` 为分段 envelope 时弹出，按段回调 `(completed, total, partName)`，并 `DoEvents` 刷新

## 流畅度评估（实现后）

| 维度 | 结论 |
|------|------|
| 多文件大工程卡顿 | 保存时按 32 MiB 切开，避免单次 `JavaScriptSerializer` 顶满 64 MiB 后失败；加载分段时 UI 有确定进度，不再「无响应式」长等 |
| 峰值内存 | 段文件各自 ≤32 MiB；合并后字典仍可能接近总证据体积（硬顶 512 MiB）。峰值主要在「解码后的 `algorithmDiagnostics` 常驻」而非单段 JSON 尖峰 |
| UI 响应 | 分段加载路径有进度条 + `DoEvents`；分析运行进度条未改。主线程仍承担解析，极大工程（数百 MB 证据）时界面可能短暂偏粘 |

### 仍可优化

1. 将段解析移到后台线程，UI 仅收进度（需理清控件线程亲和）。
2. 按需懒加载诊断（仅打开某 LOC 时解码），降低常驻内存。
3. 段内流式/增量 JSON，避免整段 `DeserializeObject`。
4. LabVIEW `ExportAnalysisSessionUtf8` 单独调用且已分段时，若未走命名存档，只有 envelope、无段文件——应引导「保存分析」；可再加明确错误码。

## 主要代码入口

- `ReviewCandidateEvidence.cs` — 打包 / 解析 / envelope
- `ReviewAnalysisArchives.cs` — 落盘段文件、读档进度、manifest 校验
- `ReviewAnalysisProgressDialog.cs` — 「读取旧有分析」
- `ReviewAnalysisMetadataBudget.cs` — 配额

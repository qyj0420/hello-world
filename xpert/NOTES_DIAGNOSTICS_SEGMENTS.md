# 分析诊断分段 + 定位大工程加载 — 格式、兼容与流畅度

构建戳：**2026-10-12T23:13:04+08:00**（与 `NOTES_DAC_REBUILD.md` / `BUILD_MANIFEST.json` 一致）。

## 现场三项目（定位算法路径）

| # | 工程 | LOC 数 | 主簇 | 次簇 | 现场现象 |
|---|------|--------|------|------|----------|
| 1 | JD01-JD02 | 69 | ≈262.5 m | ≈77.5 m | 可出结果，大工程下加载偏慢 |
| 2 | 6q03-6q04 | 117 | ≈262.5 m | ≈82.5 m | 可出结果，整理/加载易卡顿 |
| 3 | **11-12** | **462** | ≈82.5 m | ≈262.5 m | Codex 报 **数据超过约 64MB，直接没有结果**（优先修复） |

文件夹路径由现场稍后提供；本改动不依赖路径，按 LOC 规模与配额路径加固。

## 根因（第 3 项「没结果」）

定位批次在 **UI 线程** 上执行 `CompleteProjectAnalysis`：

1. **`WithCompletedAlgorithmDiagnostics` → 整表 JSON 序列化**  
   旧实现对增长中的字典反复 `Serialize`（近 O(n²)），编码逼近 **32/64 MiB** 时抛错。异常落入 `catch` 后 **整批不合并** `parameters`/`pdmap`，界面表现为「没有结果」。
2. **合并波形硬顶 `3×rows×width ≤ 1e8` 单元格**  
   462 LOC 大批次可能在诊断之前或同时触顶，同样导致整批失败。
3. **加载旧分析**全程在 UI 线程读 `pdmap.bin`（可达数百 MB）、算目录指纹、反序列化 session/诊断，表现为假死。

## 修复策略（与 32MB 分段一并落地）

| 项 | 策略 |
|----|------|
| 分段阈值 | 编码 ≥ **32 MiB** → `diagnostics-part-NNN.json` + `HVX.AlgorithmDiagnostics.Segmented/1` |
| 兼容旧数据 | 无 envelope 的扁平 JSON 仍可读（≤ **64 MiB** session 历史上限） |
| 提交不丢结果 | 诊断序列化 **best-effort**：失败也合并定位矩阵；状态提示「定位结果已保留；诊断分段延后」 |
| 打包性能 | 段打包改为估算后一次性 `Serialize`（避免 O(n²)） |
| 整理阶段 | 诊断打包 + `MergeAnalysisResultsCore` 在 **后台线程**；进度条「正在整理定位结果」 |
| 波形预算 | 合并上限 → **4e8** 单元格；x64 `pdmap` 存档 → **6.4e7** 单元格 / ~512 MiB+；目录总预算 **1.5 GiB** |
| 读档 | `LoadSavedAnalysisAsync`：IO/PrepareSession 在后台；始终显示 **「读取旧有分析」** 进度；可取消 |

## 格式约定

| 项 | 约定 |
|----|------|
| 单段文件 | `diagnostics-part-001.json` … `-064.json`（每段 ≤ 32 MiB） |
| 段内格式 | 与旧版相同的 `Dictionary<path, encodedStatus>` JSON |
| 信封 | session 内小 JSON：`schema/version/parts/recordCount/encodedBytes` |
| 未达阈值 | 仍写旧版单段扁平 JSON |
| 总硬顶 | 分段编码/解码工作集 ≤ **512 MiB**；最多 64 段 |

## 进度条入口

- 读档：`ReviewAnalysisProgressDialog.BeginReadingSavedAnalysis`（文案 **读取旧有分析**）← `LoadSavedAnalysisAsync`
- 整理：`BeginOrganizingResults`（文案 **正在整理定位结果**）← `CompleteProjectAnalysis`
- 对话框加载按钮：`await LoadSavedAnalysisAsync`

## 流畅度评估（针对定位三项目）

| 工程 | 预期 |
|------|------|
| JD01-JD02 (69) | 多在单段扁平诊断内；分析后整理与读档有进度，UI 可响应 |
| 6q03-6q04 (117) | 可能跨 32 MiB 触发分段；后台整理避免假死 |
| **11-12 (462)** | **不再因 64 MiB 诊断硬限整批丢结果**；分段落盘；合并波形预算覆盖大批次；加载走后台+进度 |

### 验证方式

1. 对 **11-12** 全选定位分析：应出现定位结果行（主/次簇量级与现场一致即可），不得再只报「超过 64M」且零结果。  
2. 保存分析：`Analysis report/<id>/` 下若诊断很大，应有 `diagnostics-part-*.json`，`session.json` 内为 Segmented 信封。  
3. 再「加载分析」：弹出「读取旧有分析」，进度前进，主界面不假死；可点取消。  
4. 旧工程（69/117 已存扁平 session）：仍能加载。  
5. 构建：`./build-sdk/build-linux.sh`。

### 仍可优化

1. 诊断按 LOC 懒解码，降低常驻内存。  
2. ApplySession / probe 控件构建仍在 UI，极大工程末段可能短暂粘滞。  
3. 段内流式 JSON。

## 主要代码入口

- `ReviewCandidateEvidence.cs` — 分段打包 / best-effort 提交  
- `ReviewProjectAnalysis.cs` — 后台整理定位结果  
- `ReviewAnalysisArchiveLoad.cs` — 后台读档 + 进度  
- `ReviewAnalysisProgressDialog.cs` — 「读取旧有分析」「正在整理定位结果」  
- `ReviewAnalysisMetadataBudget.cs` — 配额  

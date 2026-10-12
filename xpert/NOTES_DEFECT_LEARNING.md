# 缺陷识别 — 标准模型 / 拓展学习 / 冲突提示 NOTES

品牌：雄新电力 / 雄新 OWTS  
与定位侧 `NOTES_DIAGNOSTICS_SEGMENTS.md`（32 MiB 分段）同分支、互不覆盖。  
**不改动**定位算法 E+ 数学；**不削弱**急停。

## 入口

| 入口 | 控件 / 宿主 | 模型属性 |
|------|-------------|----------|
| 数据分析（PRPD / L-PRPD） | `PdReviewControl` + `ShowConfirmLearningSample` | `ModelLibraryPath` |
| 加压后分析 | `ModuleHost.ApplyPressureAnalysisSettings` | 同上（`ResolveActive`） |
| 独立「AI缺陷识别」窗口 | `CompetitionDefectHostForm` → `CompetitionDefectRecognitionControl` | 独立 preferences 的 `ModelLibraryPath` |
| Plus Data 设置 | 「缺陷识别模型文件（可选）」路径框 | 写入 `TaskMinus3.ModelLibraryPath`；**不再**暴露标准/竞赛下拉或竞赛路径 |

主页「AI缺陷识别」模块开关仍由厂家 Plus Data「显示独立缺陷识别模块入口」控制；该开关只控制入口可见性，不是第二套模型选型。

## 加载优先级

实现：`DefectModelCatalog.ResolveActive(loadedPath, buildRoot)`

1. **若用户通过路径加载了模型**（`ModelLibraryPath` / 独立窗口模型路径非空）→ **以该路径为准**（绝对路径）。
2. **若未加载** → **以标准模型为准**（`model-payload` 或 ProgramData 安装的标准 `.hxm`）。

竞赛模型字节仍可随安装包存在，且仍可通过「加载路径」指向经校验的文件；**UI 不再提供「竞赛模型」模式切换**。旧配置中的 `DefectModelKind=competition` / `CompetitionModelPath` 在 `Normalize`/`Validate` 时：

- 若标准路径为空且竞赛路径非空 → **迁移到加载路径**；
- 强制 `DefectModelKind` / `ModelKind` = `standard`，并清空竞赛路径字段。

## 拓展学习策略

文件：`ReviewDefectLearningExpansion.cs`

| 项 | 值 |
|----|----|
| 开关 | 加入学习对话框「拓展学习」复选框，**默认开启** |
| 变体数 | `DefaultVariantCount = 4`（另含原样本，合计最多 5 条写入待训练库） |
| 扰动 | 相位 ±3°（环绕 0–360）；放电量 ×(0.95–1.05)；`Scope`/`Context` 带 `expand#n` 标记 |
| 种子 | `modelSha.GetHashCode() ^ rowCount`（同模型同规模可复现） |
| 落盘 | `ReviewLearningTransactions.Add` 逐条写入；不改定位/急停路径 |

## 80% 冲突提示与重训流程

阈值：`ConflictScoreThreshold = 0.80`（类分数 0–1，即 **>80%**）。

1. 用户点击 **「加入学习」**（数据分析谱图菜单 / 独立窗口「加入学习」）。
2. `ProbeLearningModelConflict` / `ProbeCompetitionLearningConflict`：对当前可见 PRPD 跑识别，取所选标签在模型类分数中的最高分。
3. 若 **>80%** → 弹出 **「加入学习冲突提示」**：说明「保存模型会影响已有模型数据」及当前约百分比；取消则中止。
4. 用户确认后（或无冲突时直接）调用 `AddConfirmedLearningSample(..., expandLearning, conflictAcknowledged)`：
   - 可选拓展变体写入待训练库；
   - `ReviewTask12Runtime.TryRetrainBest`：按引擎暴露的 **最佳算法** 名优先（`TrainBestAlgorithm` / `RetrainBest` / …），反射调用；若运行时无训练 API → 样本保留、回执标记 `engine_trained=false`（延后重训）。
5. 学习根目录写入 `retrain-*.json` 回执（算法名、原样本数、变体数、是否引擎已训）。

未确认冲突时，服务端门禁仍会抛错，避免绕过 UI 静默覆盖。

## 关键代码

- `xpert/src/DefectModelCatalog.cs` — `ResolveActive`
- `xpert/src/ModuleHost.cs` / `ModuleHost.PressureSaving.cs` / `Program.cs` / `CompetitionDefectHostForm.cs` — 一律 `ResolveActive`
- `xpert/src/SettingsForm.PlusData.cs` / `CompetitionDefectPreferencesDialog.cs` — 去掉多模型入口
- `xpert/modules-src/DataAnalysis/src/ReviewDefectLearningExpansion.cs`
- `xpert/modules-src/DataAnalysis/src/ReviewDischargeRecognition.cs` / `ReviewDischargeRecognitionUi.cs`
- `xpert/modules-src/DataAnalysis/src/CompetitionArchiveAccess.cs` / `CompetitionDefectRecognitionControl.cs`
- `xpert/modules-src/DataAnalysis/src/ReviewTask12Runtime.cs` — `TryRetrainBest`

## 构建

`./build-sdk/build-linux.sh` → 更新 `publish/xpert-desktop-win-x64.zip` 与 `BUILD_MANIFEST` 构建戳。  
构建戳与 zip 哈希见 `NOTES_DAC_REBUILD.md`。

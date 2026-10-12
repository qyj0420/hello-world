# 多文件分析内存 / 约 64 MB 卡死 — 根因与落盘（spill）修复

构建戳：**2026-10-12T23:46:16+08:00**（Asia/Shanghai，与 `NOTES_DAC_REBUILD.md` / `BUILD_MANIFEST.json` 一致）。

现场反馈：HV-DAC Xpert 一次分析多个文件内存占用大，到约 64 MB 就卡住（11-12 工程 462 LOC 直接没有结果）。
本文记录确认的根因、旧限额、新策略和验证方法。32 MiB 分段的中间方案见 `NOTES_DIAGNOSTICS_SEGMENTS.md`（已被本方案取代，仅保留读取兼容）。

## 1. 根因（现场构建 `be70340`）

每个 LOC 文件分析完成后，算法返回一份完整的 per-file 状态（`ReviewAnalysisResult.Status`，E/E+ 时内含压缩账本，单份 ≤ 1 Mi 字符、账本解压 ≤ 16 MiB）。
这些状态被**全部解码后常驻**在 `PdReviewControl.algorithmDiagnostics`（`Dictionary<相对路径, 完整状态>`），每次提交都把整张表重新校验、编码成一个 JSON。

### 调用链

1. `ReviewProjectAnalysis.cs:206` 后台 `Task.Run` 逐文件 `ReviewAnalysisEngine.Analyze(request)`（:212），完整 `Status` 留在 `List<CompletedFile>` 里直到整批结束。
2. `PostAnalysis → CompleteProjectAnalysis`（UI 线程，:248）。
3. :269 `WithCompletedAlgorithmDiagnostics(good)` → `SerializeCurrentAlgorithmDiagnostics` → `SerializeAlgorithmDiagnosticsCore`（`ReviewCandidateEvidence.cs`）：对**旧表 + 本批**所有状态累计解码字节、E 账本校验字节，并生成整表 JSON。
4. 任一限额超出即抛异常 → 落入 `CompleteProjectAnalysis` 的 catch → **整批不合并**（`parameters`/`pdmap` 都不更新），界面只剩失败提示，看起来就是"到 64M 卡住、没结果"。

### 触发的常量与报错原文

| 位置（`be70340`） | 常量 | 值 | 报错 |
|------|------|----|------|
| `ReviewCandidateEvidence.cs:22` | `CandidateDecodedBytesLimit` | **64 MiB**（所有已分析文件解码后状态之和） | `Algorithm diagnostics exceed the decoded session budget; split the analysis batch.`（:109） |
| `ReviewAnalysisMetadataBudget.cs:8` → `CandidateEvidenceLimit` | `EncodedDiagnosticsBytes` | **32 MiB**（整表编码 JSON） | `Algorithm diagnostics exceed the bounded 32 MiB session budget; split the analysis batch.`（:113，读档 :120） |
| `ReviewAnalysisMetadataBudget.cs:9` | `SessionMetadataBytes` | **64 MiB**（session.json / `MaxJsonLength`） | `Session metadata exceeds its size limit.` |
| `ReviewCandidateEvidence.cs:26` | `CandidateLedgerValidationBytesLimit` | 512 MiB（E 账本累计校验量） | `Combined E ledger validation exceeds the bounded 512 MiB session work budget; split the analysis batch.`（:138） |
| `ReviewCandidateEvidence.cs:21` | `CandidateStatusCharacterLimit` | 1 Mi 字符 / 文件 | `Invalid algorithm status size.`（单文件上限，未改） |
| `ReviewAnalysisArchives.cs:114` | 存档总量 | 512 MiB | `Archive member or total size exceeds the memory/read budget.` |

### 为什么内存远大于 64 MB

- `algorithmDiagnostics` 持有全部解码状态（UTF-16，内存约为 UTF-8 字节数的 2 倍）。
- `encodedCandidateStatuses` 缓存又保存一份 `Source`（原文）+ `Encoded`（gzip+base64），每次提交整表重建。
- `CaptureNamedAnalysisRollback` 调用 `ExportAnalysisSession` 生成整表 JSON 后再 `PrepareSession` 全量解析一次。
- `ReviewEAnalysis.CreateTimeFrequencyDisplay` 把所有文件的账本解压后缓存在 `ledgers` / `eventMaps`（:133），E/E+ 每文件最多 16 MiB。

所以限额只是表象：真正的问题是"所有文件的完整证据永久常驻、每次整表处理"。

中间版本 `c0abbe2`/`fda7ed8`（32 MiB 分段写 `diagnostics-part-NNN.json`，上限 512 MiB）解决了"超限就整批丢结果"，但解码状态仍全部常驻，诊断序列化失败时只能把整张表留在内存里（提示"诊断分段延后"）。

## 2. 新策略：逐文件落盘 + 引用 + 按需恢复

| 环节 | 现在的做法 |
|------|------------|
| 分析中 | 后台 worker 每完成一个文件就 `RetainCompletedStatus`：校验 E 账本 → gzip → 写入 spill 存储 → `result.Status` 清空。整批任何时刻都不累积完整状态。 |
| spill 存储 | `ReviewDiagnosticSpill`（新）：进程级、按 SHA-256 内容寻址；每个状态一个 `<sha>.gz` 放在 `%TEMP%\HVX.DiagnosticsSpill\p<pid>-<guid>\`（可用环境变量 `HVX_DIAGNOSTICS_SPILL_ROOT` 改根目录）。`.owner` 文件锁 + 启动时清理已退出进程的残留目录；进程退出删除自己的目录。 |
| 会话字典 | `algorithmDiagnostics` 只存约 100 字符的引用 `HVX.Diagnostics.Spill.v1:<SHA256>:<解码字节>:<账本字节>`；≤ 4 KiB 且无 E 账本的小状态仍内联。 |
| 查看文件 | `ResolveAlgorithmStatus` 读取 → 解压到**精确长度** → 校验 SHA-256，无损还原。时频显示按文件分组，一次只展开一个文件的账本，用完即释放。 |
| 释放 | 引用字符串被 GC 回收（租约 finalizer）后，条目在下一次清扫（每 64 次写入或达到上限时强制 GC）时删除磁盘文件。回滚快照、探针控件、显示缓存只要还持有引用，条目就不会被删。 |
| 合并 / 预算 | 后台整理只拼引用表；`CheckAlgorithmDiagnosticsBudget` 用引用里的字节数核对上限，不解码。 |
| 保存分析 | `session.json` 写引用索引（≤ 32 MiB，100 文件约 11 KB）；被引用的压缩状态写入同目录 **`diagnostics.bin`**（`HVXDIAG1` + 索引 + 载荷），列入 manifest 并校验 SHA-256。 |
| 加载分析 | 逐条从 `diagnostics.bin` 读取、解压、校验长度 / SHA-256 / E 账本字节后放回 spill 存储；每 64 条回调进度（可取消）。session 有引用但 manifest 未列 `diagnostics.bin` → 拒绝。 |
| 回滚 | `CaptureNamedAnalysisRollback` 导出引用（不解码）并直接复用当前引用表。 |
| 宿主自持会话 | 公共 `ExportAnalysisSession(Utf8)` / `AlgorithmDiagnosticsJson` 仍输出自包含 JSON：逐文件解压编码；> 32 MiB 时使用内联分段信封，总量 ≤ 64 MiB（宿主重新导入的上限）。超过请用"保存分析"。 |
| 旧数据 | 扁平 JSON（≤ 64 MiB）、32 MiB 分段 part 文件、内联分段信封均可读；读入时逐条解码校验后落盘。新保存不再写 `diagnostics-part-*.json`。 |
| 单文件落盘失败 | 例如达到 1 GiB 上限：该文件标为失败（"算法证据无法保存，本文件未合并"），其余文件照常合并。不会出现"有定位结果但证据丢失"。 |

### 限额对照（全部仍为有限值）

| 项 | 旧（`be70340`） | 新 |
|----|------------------|----|
| 常驻解码状态 | 全部文件之和，≤ 64 MiB 后报错 | **一次只解码 1 个**（≤ 4 MiB 状态 + ≤ 16 MiB 账本） |
| 会话可引用的解码状态总量 | 64 MiB | `DecodedDiagnosticsBytes` **2 GiB**（只按引用元数据计数） |
| E 账本累计校验量 | 512 MiB | `LedgerValidationWorkBytes` **8 GiB**（逐个展开，不常驻） |
| spill 存储绝对上限（压缩后） | — | `DiagnosticsSpillBytes` **1 GiB**；临时目录不可写时内存回退 `DiagnosticsSpillMemoryFallbackBytes` **256 MiB** |
| 会话字典中的诊断编码 | 32 MiB 整表 | 引用索引 ≤ 32 MiB |
| 自包含导出 | 32 MiB | 64 MiB（内联分段） |
| 存档 `diagnostics.bin` | — | `DiagnosticsArchiveBytes` **1 GiB**（逐条读取，不计入 1.5 GiB 整读预算；目录指纹上限 1.5 GiB + 1 GiB） |
| 单文件状态 | 1 Mi 字符 | 不变 |
| 单个 E 账本 | 16 MiB 解码 / 512 KiB 压缩 | 不变 |

未改动：报告附件 `ReportChartBytesLimit` 64 MiB、ReportGenerator 读取 `session.json` 的 16 MiB 上限（引用索引使 `session.json` 大幅变小，反而更容易满足）、合并波形 / pdmap 预算（见 `NOTES_DIAGNOSTICS_SEGMENTS.md`）。

## 3. 改动文件

| 文件 | 改动 |
|------|------|
| `modules-src/DataAnalysis/src/ReviewDiagnosticSpill.cs`（新） | spill 存储：引用格式、写入/读取/校验、租约与清扫、目录与上限 |
| `modules-src/DataAnalysis/src/ReviewDiagnosticArchivePack.cs`（新） | `diagnostics.bin` 写入与逐条导入校验 |
| `modules-src/DataAnalysis/src/ReviewAnalysisMetadataBudget.cs` | 新增 `DecodedDiagnosticsBytes` / `LedgerValidationWorkBytes` / `DiagnosticsSpillBytes` / `DiagnosticsSpillMemoryFallbackBytes` / `DiagnosticsArchiveBytes` |
| `modules-src/DataAnalysis/src/ReviewCandidateEvidence.cs` | 引用表、按需解析、自包含/引用两种序列化、统一解析（扁平 / 分段 / 引用）；删除整表缓存与待写 part 队列 |
| `modules-src/DataAnalysis/src/ReviewProjectAnalysis.cs` | worker 逐文件落盘；后台整理只拼引用并核对上限 |
| `modules-src/DataAnalysis/src/ReviewEAnalysis.cs` | `CreateTimeFrequencyDisplay` 逐文件解析、释放账本 |
| `modules-src/DataAnalysis/src/ReviewAnalysisSession.cs` | `ExportAnalysisSession(Utf8)Core(..., referenceDiagnostics)` |
| `modules-src/DataAnalysis/src/ReviewAnalysisArchives.cs` | 保存写 `diagnostics.bin`；manifest 接受该成员 |
| `modules-src/DataAnalysis/src/ReviewAnalysisArchiveLoad.cs` | 要求引用对应的 `diagnostics.bin` 在 manifest 中；进度标签 |
| `modules-src/DataAnalysis/src/ReviewArchiveRollback.cs` | 回滚导出引用，不再整表解码 |
| `modules-src/DataAnalysis/src/ReviewAnalysisOverwrite.cs` | 路径检查包含 `diagnostics.bin`；目录指纹上限 |
| `tests/AnalysisDiagnosticSpillOfflineSmoke.cs`（新） | 见下 |
| `tests/LocationELedgerBudgetOfflineSmoke.cs`、`tests/AnalysisBatchEvidenceSmoke.cs` | 改为解析引用后比较；新预算边界；bomb 用例真正走到累计解压上限 |

## 4. 验证

### 本环境已做（离线、合成数据，未运行产品 / SDK / 设备）

```bash
cd xpert
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build build-sdk/modules/DataAnalysis/DataAnalysis.csproj -c Release   # 0 错误
D=build-sdk/modules/DataAnalysis/bin/Release/HVX.PdReview.V1.0.dll
mcs -r:System.Web.Extensions -out:/tmp/spill.exe tests/AnalysisDiagnosticSpillOfflineSmoke.cs
mono /tmp/spill.exe $D /tmp/spill-qa
mcs -r:System.Web.Extensions -r:System.IO.Compression -r:$D -out:/tmp/ledger.exe tests/LocationELedgerBudgetOfflineSmoke.cs
mono /tmp/ledger.exe $D /tmp/ledger-qa
```

| 测试 | 结果 |
|------|------|
| `AnalysisDiagnosticSpillOfflineSmoke` | **PASS 130 项**：100 个文件、解码共 **92,163,390 字节（> 64 MiB 旧上限）**分 3 批合并成功；会话字典只含引用；合并后托管内存增量 **约 350 KB**；spill 目录 100 个 `.gz` 共 52.5 MB；100 个文件逐一字节级还原；`diagnostics.bin` 往返、释放后从存档恢复、篡改载荷被拒、未篡改条目仍可读；> 32 MiB 自包含导出走内联分段并无损重载、> 64 MiB 被拒；32 MiB 分段 part 存档可读；2 GiB / 8 GiB / 1 Mi 字符上限按引用元数据生效 |
| `LocationELedgerBudgetOfflineSmoke` | **PASS 92 项**（31 个 1 MiB 账本分批合并、保存/重载逐字节一致、8 GiB 校验预算边界、解压炸弹） |
| `AnalysisBatchEvidenceSmoke` 静态部分 | **PASS 12 项**；GUI 部分在 mono WinForms 下无法构造控件（GDI+ 环境限制），需在 Windows 运行 |

### 现场验证步骤（Windows）

1. 打开 11-12（462 LOC），全选做定位分析：应正常出结果，不再停在约 64 MB；任务管理器中进程内存不随文件数线性增长（每个文件的证据在 `%TEMP%\HVX.DiagnosticsSpill\p<pid>-*\*.gz`）。
2. 点开任意已分析文件 / 切到时频（E/E+）视图：诊断与特征应与单文件分析时一致。
3. 保存分析：`Analysis report/<id>/` 下应有 `diagnostics.bin`，`session.json` 很小；关闭程序后 spill 目录被删除。
4. 重新打开程序加载该分析：显示"读取旧有分析"进度，结果与诊断完整；用旧版本保存的分析（扁平或 `diagnostics-part-*.json`）也能加载。
5. 可选：设置 `HVX_DIAGNOSTICS_SPILL_ROOT=D:\HVXSpill` 把临时文件放到大盘。

## 5. 风险 / 后续

- 依赖临时目录可写；不可写时退回 256 MiB 内存存储，超过即单文件失败（有明确提示）。
- 合并波形（`MergedWaveformCells` 4e8）与 pdmap 仍按原方案常驻，这是定位结果本身，不在本次"诊断证据常驻"问题范围内。
- 未在 Windows 实机上测内存曲线；以上内存数字来自 mono 离线测试。

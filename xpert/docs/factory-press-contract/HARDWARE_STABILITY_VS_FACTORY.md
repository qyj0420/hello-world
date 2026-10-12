# 硬件稳定性对照：Factory V1.0.30.14 vs dac-rewrite Acquisition / HvPressTest

> 生成日期：2026-10-12（Asia/Shanghai）  
> 对照源：`/tmp/hw-diag/…V1.0.30.14_导出包_20261012`（export-manifest：`1.0.30.14`，工程 `FactoryAcceptanceTester_R21`）  
> 当前栈：`/workspace/dac-rewrite/Xiongxin.Owts.Acquisition` + `Xiongxin.Owts.HvPressTest`  
> 范围：**只谈加压+采集可靠性**；不把 `LIVE_VALIDATION_RULES` 当验收架构；不反推第三方 LabVIEW。

---


> **2026-10-12 更新（DesktopUi / Acquisition）**：§5.1–5.6 已落地：`DeviceSdkLease` / `_diGate` / `PicoCaptureCooldown` / `IdleDiRebuildPolicy` 经 `LiveEmergencyMonitor` 空闲路径接线 / `ArtFreshFeedbackWindow`（AO 后新鲜窗+积压关断）。DesktopUi FakeLive/实机组合根走同一硬化路径并打品牌「雄新电力」。仍开：故障分类落盘完善。HvPressTest 双图由另一轨负责。

## 1. 这个 V1.0.30.14 包是什么

| 项 | 内容 |
|---|---|
| 身份 | 雄新电力 · 出厂/板卡验收机 **工作源码快照**（`ExportType`: 非保护版精确映射） |
| 版本标签 | `1.0.30.14`，导出日 2026-10-12 |
| 形态 | WinForms + 离线测试 + `Imported/` ART/Pico 模块；**不含驱动/编译器/混淆映射/现场记录** |
| 意图（AGENTS.md） | 把反复出现的 ART/Pico「测控报错」固化成可测策略：独占会话、P0.4 独立只读、空闲 DI 重建一次、Pico 长句柄+冷却、故障分类锁存、清理失败禁止自动重开、异常先 HV OFF/AO=0 |
| 硬件约定 | ART USB3135 DB37：P0.0 HVON、P0.3 加压灯、P0.4 急停、P0.5 连接灯、P0.7 光触发；Pico 5242D **单次 Open**；抑制 PicoScope.exe 启动窗 |
| 与 LabVIEW | 目标行为是 **LabVIEW 式 DAQ：长会话、独占、短 DI 轮询不与 AO/AI 打架**；本包是 C# 侧已经踩坑后的实现，不是 LabVIEW 源码 |

核心硬件文件（应优先读）：

- `NativeFactoryBackend.cs` — 会话/轮次/任务句柄所有者  
- `FactoryEmergencyMonitor.cs` + `FactoryIdleDiRebuildPolicy.cs` — P0.4 串行监视与空闲重建  
- `FactoryDeviceLease.cs` — 进程间 SDK 独占锁  
- `FactorySessionFault.cs` — 故障分类与清理失败门闩  
- `FactoryCaptureCooldown.cs` + `Imported/PicoInputCapture.cs` — Pico 冷却与句柄进出  
- `FactoryArtFeedback.cs` / `FactoryIoTiming.cs` — 升压反馈新鲜窗口与耗时诊断  
- `FactoryResidentDeviceServer.cs` / `FactoryResidentDevices.cs` — UI 侧常驻工作进程（**加压验证不必整包迁**）

---

## 2. Top 5 根因（来自该包代码/注释/测试，解释「反复硬件/测控错」）

### R1. ART 上 DI 与 AO/AI 争用同一驱动调度（最像 LabVIEW 做得好、C# 易翻车处）

- Factory 明确：**Runner 独占 AI/AO/DO**；**独立 10 ms 请求节拍的监视线程只读自己的 P0.4 DI 任务**，从不写输出、不清别人的任务（`NativeFactoryBackend` / `FactoryEmergencyMonitor` 头注释）。  
- DI 单次超时 **300 ms**，AI/AO/DO 仍 **20 ms**；**同一 DI 不允许并发堆积**；阻塞期间不再每 10 ms 起新读。  
- 超时/失败 = **未知**，**禁止用历史 HIGH 放行**；样本年龄 >300 ms 同样锁存。  
- **空闲**连续明确超时后，仅允许 owner 在「无输出」下 **每会话一次** DI 任务重建（`FactoryIdleDiRebuildPolicy`）。

→ 典型症状：加压中或升压节拍里偶发 P0.4 超时 → 被当成断线/急停/测控失败；或反过来用过期高电平误放行。

### R2. Pico 句柄「有进有出」破坏 + 采后冷却缺失

- 约定：单会话对象持有 handle；**正常轮次不 Open/Close**；只有 Open 产生新 handle；Close 成功才清空；失败保留所有权。  
- `FactoryCaptureCooldown`：新 Open 后 **≥2 s** 保护；Stop/缓冲释放后共享单调时钟冷却；带压 Arm **冷却不足立即失败**（不等待）；新段硬上限 60 ms。  
- `PICO_BUSY 0x27`：已布置未完成时 **不是断线**；非预期 BUSY 停本轮、不自动重连/重加压（`PicoInputCapture.HandleTransportStatus`）。  
- 迟到旧回调用 **acquisitionGeneration** 隔离。

→ 典型症状：连续加压第二档起偶发 OpenUnit/Stop/缓冲错误、BUSY 被记成断线、冷却不够就 Arm。

### R3. ClearTask/Close 失败被「吞掉」后仍当已释放 → 二次 Open 撞幽灵句柄

- Factory `ArtUsb3135Device.ClearTask`：**Clear 失败则保留 handle**，禁止假装已释放；Dispose 可重试；清理失败写 `cleanup-error` / `CleanupError`，**禁止自动重开**（`FactorySessionFaultStore`）。  
- 设备锁 `FactoryDeviceLease`：清理失败时 **不释放全局 Mutex**（`GC.KeepAlive`），避免下一进程以为设备空闲。

→ 典型症状：上一轮「看似关了」实际 ART 任务仍在 → 下一轮 CreateTask/StartTask 测控报错、DeviceNotReady。

### R4. 会话边界不清：轮次重叠 / 身份漂移 / 跨进程双开

- `FactoryNativeSessionPolicy.SameDeviceSession`：ART/Pico 序列号 + 运行库路径必须一致才可复用。  
- `CanReuseSession = SessionOpen && !roundActive && !faulted`；故障锁存后不能开新轮。  
- `Global\Xiongxin.FactoryAcceptance.DeviceSdk.v1` Mutex：枚举/诊断/常驻/手动入口都拿同一 SDK 锁；**AbandonedMutex → 拒绝打开**（关断状态未知）。  
- OpenSession 顺序：**先 Pico 身份成功，再创建任何 ART 输出任务**。

→ 典型症状：LabVIEW/旧验收机/ PicoScope.exe / 另一 C# 实例同时占卡；或故障后 UI「再试一次」复用坏会话。

### R5. 升压反馈用了积压/过期 AI 窗口（看似驱动错，实为控制时序）

- 正 AO 写入后必须读 **新边界之后的完整反馈窗**；积压 >1 s 或追读 8 组仍积压 → 禁止继续升压；单次最多等 10 个新点；最终 pending≤20（`FactoryArtFeedback`）。  
- 原始峰值（含半码阶）参与过压/过流；无效/饱和不平均放行。

→ 典型症状：USB 抖动时「过压/过流/无效反馈」抖动报错，或升压超调后保护跳闸，被归咎为驱动不稳。

---

## 3. Gap 表：Factory 已学到的 vs 当前 HvPressTest / Acquisition

| 能力 | Factory V1.0.30.14 | 当前 dac-rewrite | 间歇故障关联 |
|---|---|---|---|
| DI 与 AO/AI 解耦 | DI 独立任务+独立线程；不经输出 owner 的写路径 | `LiveEmergencyMonitor` → `ReadEStopSample` → **与 AO/AI/Pico 共用 `_sync`**；忙则跳过→Unknown | **高**：升压/块采集时 DI 饿死或整锁阻塞 |
| DI 超时语义 | 300 ms 单次；样本年龄门闩；空闲一次串行重读 | 已有 ~300 ms 与「未知不冒充低」；**无样本年龄门闩、无空闲重读/重建** | 中高 |
| 空闲 DI 重建 | `FactoryIdleDiRebuildPolicy` 每会话一次 | `IdleDiRebuildPolicy` + `LiveEmergencyMonitor` 空闲重读/重建（加压禁用） | 中（长待机后 DI 卡死） |
| 进程间独占 | `FactoryDeviceLease` Global Mutex | 仅 `SingletonAcquisitionSession` **进程内** | **高**（与 LabVIEW/旧程序并存时） |
| Pico 长句柄 | Open 一次，轮间 Configure/Stop | 会话级 Open；有 `ClosePicoOnly`/`TryReopenPico` | 中：重开路径吞异常且无冷却 |
| Pico 冷却 / BUSY | `FactoryCaptureCooldown` + 0x27 分类 | 仅档间 `InterRoundCooldownMilliseconds`；**无 MarkOpened/release-pending、无 PingUnit/0x27** | 中高 |
| Clear 失败保留句柄 | Clear 失败不置 Zero；清理失败禁重开 | `ArtPicoLiveBackend.Clear` **一律 catch 后 task=0** | **高** |
| 故障分类 | `FactorySessionFaultCategory` 冻结首因 | `SessionFault` 落盘 + CleanupError 禁重开（无 MessageBox）；首因冻结 | 低（真机待验） |
| AO 后新鲜反馈窗 | `requireFreshWindow` + 积压上限 | `ArtFreshFeedbackWindow`：写 AO 后丢弃边界前样本；积压超限 → HardwareFault 关断 | 中（超调/误保护） |
| 轮次 vs 会话 | OpenSession / BeginRound / EndRound；故障不可复用 | `RunCycleAsync` 单循环；Singleton 可复用但 **清理失败仍可再 Open** | 中 |
| HV OFF on fault | 异常先关断；不自动恢复加压 | Live 路径已有 ForceSourceOff / 急停锁存 | 已有，保持 |
| Io 时序诊断 | `FactoryIoTiming` → io-timing.tsv | 无等价诊断 | 低（排障用） |
| 常驻子进程 | Resident server/client | 无（HvPressTest 不需要） | 勿整包迁 |

---

## 4. 已半存在、可做的小赢（不必整迁 Factory）

| 路径 | 现状 | 建议小改 |
|---|---|---|
| `Acquisition/Live/LiveEmergencyMonitor.cs` | 独立线程 + 边沿回调已有 | 改为调用 **不抢 `_sync` 的 DI 快路径**；增加样本时间戳/最大年龄；加压中 EnterActive（禁空闲重试） |
| `Acquisition/Live/ArtPicoLiveBackend.cs` `_diReadBusy` | 叠读跳过→Unknown | 保留；把 DI 读移出与 AO/AI 的长临界区（或 DI 用独立短锁） |
| 同上 `Clear(ref IntPtr)` | 吞异常并清零 | **Clear 失败保留句柄 + 置 cleanupFailed**；禁止后续 Open |
| `SingletonAcquisitionSession.cs` | 进程内单开 | 叠加 **Global Mutex 租约**（可移植 `FactoryDeviceLease` 精简版） |
| `HvPressTest/PressTestRunner.cs` 档间冷却 | 已有 ≥2 s | 对齐 Factory：冷却绑定 **Pico Stop/缓冲释放确认**，不仅仅是 Thread.Sleep |
| `NativeInterop.cs` `SuppressStartupWindow` | 已有 | 保持；Open 前调用 |
| `LiveAcquisitionSession` HV OFF / latch | 已有 | 保持；补充故障类别枚举，勿在清理失败后静默重开 |
| Finite AI 切换 | 已对照 ArtUsb3135 清监测再布防 | 保持；失败路径勿吞掉 RestartMonitoring 失败 |

---

## 5. 建议下一步（仅 HvPressTest + Acquisition，按影响排序）

1. **拆锁：P0.4 DI 真正独立于 AO/AI/Pico `_sync`**  
   - 改 `ArtPicoLiveBackend.ReadEStopSampleUnlocked`：DI 读只用 `_diGate`（或 Interlocked + DI 句柄），**禁止**在持有 `_sync` 时做 300 ms DI 读。  
   - `WriteAo`/`ReadFeedback`/`ArmBlockCapture` 路径上的急停检查改为读 **监视缓存**（带年龄），过期=阻断，不是再进 SDK。  
   - 对照：`FactoryEmergencyMonitor` + `NativeFactoryBackend.ReadEmergencyCore`。

2. **Clear/Close 诚实所有权 + 清理失败禁重开**  
   - 移植 `ArtUsb3135Device.ClearTask` 语义到 `ArtPicoLiveBackend.Clear`；Pico `CloseUnit` 失败保留 `_pico`。  
   - `SingletonAcquisitionSession` / `PressTestRunner`：cleanupFailed → 进程退出码非 0，**禁止**同进程再 Open。  
   - 对照：`FactorySessionFault` CleanupError、`FactoryDeviceLease.ReleaseAfterCleanup`。

3. **进程间 DeviceLease（精简）**  
   - 新建 `Acquisition/Live/DeviceSdkLease.cs`（拷 `FactoryDeviceLease` 去 UI 依赖）；`ArtPicoLiveBackend.Open` / HvPressTest 入口 Acquire；Abandoned → 明确中文错误。  

4. **Pico 冷却状态机 + BUSY 分类**  
   - 移植 `FactoryCaptureCooldown`（可放 `Acquisition/Live/PicoCaptureCooldown.cs`）；`ArmBlockCapture` 前 `EnsureReady`；Open/`Stop`/`Abort` 打 Mark*。  
   - 增加 `PingUnit`；0x27 期望中=忙，非期望=停轮不重连。  
   - HvPressTest 档间冷却改为等 cooldown，而不是固定 sleep 冒充释放完成。

5. ✅ **空闲 DI：一次超时重读 + 一次重建**（`LiveEmergencyMonitor` + `IdleDiRebuildPolicy`；加压禁用）

6. ✅ **AO 后新鲜反馈窗**（`ArtFreshFeedbackWindow`；积压超限 → HardwareFault + HV OFF）

7. ✅ **故障分类落盘（无弹窗）**  
   - `SessionFault` 精简分类 + DesktopUi/Live 写读 `session-fault.json`/`cleanup-fault.json`；CleanupError 禁重开；UI 不 MessageBox。  
   - 自动恢复边界：**只恢复只读待机，绝不自动恢复加压**（Factory 已确认）。

---

## 6. 禁止照搬的危险模式

| 模式 | 为何危险 |
|---|---|
| `MessageBox` 挡在清理失败路径（Resident FormClosing） | HvPressTest/无头会卡死；清理失败应退出码+日志 |
| 整包 `FactoryResidentDevice*` 子进程架构 | 对加压验证过重；文件心跳易引入新竞态；应先把同进程会话做稳 |
| 空 `catch { }` 后把句柄置 0 / 继续 Open | 制造幽灵任务，正是测控间歇错温床 |
| 用历史 HIGH / 灯亮 / 心跳成功 **冒充** 加压许可 | Factory 反复禁止；只能当诊断 |
| 故障或断线后 **自动恢复高压/自动续压** | 与用户确认边界相反；仅允许显式重开批次 |
| 无限 DI 重建 / 加压中重建 | 会清掉真实 LOW 或与输出并发清任务 |
| AbandonedMutex 后仍打开设备 | 关断状态未知 |
| 把 `PICO_BUSY` 当断线并 Close+Open | 破坏长句柄约定，放大 USB 不稳定 |
| 反推/复制第三方 LabVIEW 二进制逻辑 | 超出任务；以本 C# 包已验证策略为准 |
| 把 `LIVE_VALIDATION_RULES` 当本加压路径验收架构 | 用户明确排除 |

---

## 7. 一句话结论

Factory V1.0.30.14 不是「功能更多的 UI」，而是一份 **ART/Pico 可靠性作战手册的可运行实现**：独占租约、DI 与输出解耦、诚实句柄、冷却与 BUSY 分类、故障锁存且清理失败禁重开。当前 dac-rewrite 已具备 Singleton、P0.4 监视骨架、HV OFF、档间冷却和启动窗抑制，但 **DI 仍与 AO/AI 抢同一把锁、Clear 失败被吞、无跨进程租约、无 Pico 冷却/BUSY 语义**——这几项最足以解释「相对 LabVIEW 差一截的间歇测控错」。优先做 §5.1–5.4，不要整迁 Factory UI/常驻进程。

---

## 8. 关键源文件速查

**Factory：**  
`AGENTS.md`, `export-manifest.json`, `NativeFactoryBackend.cs`, `FactoryEmergencyMonitor.cs`, `FactoryIdleDiRebuildPolicy.cs`, `FactoryDeviceLease.cs`, `FactorySessionFault.cs`, `FactoryCaptureCooldown.cs`, `FactoryArtFeedback.cs`, `FactoryIoTiming.cs`, `FactoryNativeSessionPolicy.cs`, `FactoryConnectionWatch.cs`, `FactoryTriggerRecovery.cs`, `FactoryP04Diagnostic.cs`, `Imported/PicoInputCapture.cs`, `Imported/Task38/ArtUsb3135Device.cs`, `Imported/Task38/PicoScope5242DBlockDevice.cs`

**Rewrite：**  
`Xiongxin.Owts.Acquisition/Live/ArtPicoLiveBackend.cs`, `LiveEmergencyMonitor.cs`, `LiveAcquisitionSession.cs`, `NativeInterop.cs`, `SingletonAcquisitionSession.cs`, `Xiongxin.Owts.HvPressTest/PressTestRunner.cs`, `PressTestConfig.cs`, `README.md`

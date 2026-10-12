# FactoryPress 漏洞与不足审计（只查不改）

**时间：** 2026-10-12  
**范围：** `factorypress/` — HvPressTest / Acquisition / Contracts / HostAdapters / publish / docs  
**审计：** Claude OP5.5（只读）

**前提：** 文档将 P0.4 描述为急停盒检测；若现场另有硬切断 HVON/高压源，Critical #1/#2 可降级。否则为人身安全风险。

---

## 严重 / Critical

1. **急停仅软件轮询，无看门狗；崩溃/卡死不归零 AO/HV**  
   `LiveEmergencyMonitor` 轮询；`Program` 未处理异常只写日志；无 `ProcessExit`→AO=0/HVON=0。进程被杀时 ART 可能保持最后输出。

2. **急停读失败 fail-open；锁存可被“已释放”自动清除**  
   `ReadEStopPressed` 在未知时返回 false；`RampUntilExit` / `PrepareNonZeroStartup` / `SleepCancellable` 多处 `catch { pressed=false }`；UI `TryAutoClearEmergencyLatchUi` 周期性清锁存。接触不良时可能再次写入非零 AO。

3. **增益几乎不校验；过压只看分压，无交叉校验**  
   仅拒绝增益=1000；GUI 可到 100000；`AbsoluteOverVoltageLimitV=0`；过压/达压只看 AI4。分压开路时可能一直升到 AO 上限。发布包仍含 DEPRECATED `bench-cal-test-2000.json`。

## 高 / High

4. **校准锁定形同虚设** — `PrepareInterlocks` 无条件 `SetCalibrated(true)`。  
5. **急停后仍可能闭合 P0.7 抓波**；`EnterActiveMode` 拒绝被 `catch {}`；monitor 可能未运行。  
6. **`Close()` 写 AO=0 失败仍标影子为已归零**，不置 cleanupFailed；`ConfirmAoZero` 不读回硬件。  
7. **自动量程重试可反复加压**；`MaxRangeRetries` 无硬上限；瞬时重试靠中文子串匹配；中间尝试不写 CSV。  
8. **多轮 stem 冲突覆盖文件**；左右波形可能错配；无 PD 标定时默认 2000 pC/V 出数。  
9. **`DeviceSdkLease` 可被绕过**；AbandonedMutex 放行标志与测试辅助进入生产路径。

## 中 / Medium

10. cleanup 锁存在 `%TEMP%`，易丢。  
11. Pico 冷却按 60 ms 封顶估算，与 UI 最长 120 ms 不一致。  
12. UI 门禁靠中文文案匹配；关闭窗体不候后台加压任务；`_latched` 非 volatile。  
13. JSON 配置手写解析无 schema；标定文件分压/电流可被默认覆盖。  
14. FakeLive 覆盖不到急停 fail-open / 冷却 / lease；无测试工程。  
15. 原生 DLL 裸名加载、无签名校验。

## 低 / 工程不足

16. 原 `Xiongxin.Owts.sln` 引用缺失项目；HostAdapters 依赖 Persistence/Algorithms/Ai（包内无源）。  
17. 文档/帮助中增益 2000 vs 6000 不一致。  
18. 三份同 hash exe 靠文件名切换模式。  
19. 发布含 pdb/createdump；无 Art_DAQ/ps5000a（有意）。  
20. 源码带 `.bak` 备份文件。  
21. 清单引用不存在的 `LIVE_VALIDATION_RULES.md`。  
22. 崩溃日志写桌面（无密钥）。  
23. 全局 Mutex 默认 ACL。  
24. Nullable 关闭、部分超时默认值偏短。

---

## 建议优先修复（Top 5）

1. 急停 **fail-closed** + 锁存仅人工复位；禁止读失败当“未按下”。  
2. 确认/加装硬件急停切断 + 进程退出时 AO=0/HVON=0；最好有硬件看门狗。  
3. AO×增益 / VMON / 分压 **交叉校验** + 非零绝对过压限；增益白名单；删 2000 弃用配置。  
4. 去掉假校准；`Close` 写失败置 cleanupFailed 且勿伪造影子归零。  
5. 限制并记录所有重加；按 `ExitReason` 分类；多轮 stem 加轮次；无标定则不报 pC。

## 已较扎实的部分

写非零 AO/HVON 前检查急停缓存新鲜度；加压前 AO=0 再读 P0.4；周期 `finally` 强制关源；HVON↔灯同步；残压等待；Live 二次确认；cleanupFailed 与双设备身份；量程下调限档；拒绝增益占位 1000。

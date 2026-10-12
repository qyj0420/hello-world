# 现场就绪说明（FIELD_READY）

**日期：** 2026-10-12（Asia/Shanghai）  
**仓：** `/workspace/dac-rewrite` · 品牌 **雄新电力 / 雄新 OWTS**  
**现网 dac-source 壳不改。本轮不 sync 到用户 PC。**

## 原则（用户定案）

1. **GUI 冻结** — DesktopUi MainForm 布局/控件/外观不改版。
2. **定位 E+ 冻结** — Task17 / 算法映射（含已有 E+ 修正）不改。
3. **硬件 = 出厂加压路径** — 共用 `Acquisition` Live（`PressTestRunner` / FactoryPress 同源）。
4. 实现/运行时可改，只为更稳、更流畅。

详见：`docs/HARDWARE_PATH_ALIGNMENT.md`（硬件对齐）、`docs/PRESS_MERGE.md`（出厂加压融合）。

## 当前已就绪（仓内 / FakeLive 已绿）

| 项 | 说明 |
|---|---|
| DeviceSdkLease | 进程独占；干净 Close 可再 Connect |
| Pico 单次 Open | 校准+加压共用 Singleton；禁二次 Open |
| 双序列号 Open 硬门 | 期望 ART/Pico 不匹配 → 回滚 Open（P0.5↓、释租约） |
| 校准 Rapid | Live/FakeLive：`RunFactoryAlignedCalibration`（ChannelA Rapid + 自适量程） |
| 连续加压 | DesktopUi「连续加压次数」→ **恒** `RunFactoryAlignedPress`（量程自调 + Pico 冷却）；仿真仍 `RunCycle` |
| 停止→再压 | `RequestOperatorAbort` + 清软锁存 + 每批重建 CTS |
| pd-calibration.json | `{AppBase}/config/`；无效默认 **2000** pC/V |
| 台架 / HardOC | 「台架标定…」Connect 后可 Apply；HardOC **0.012 A**；无需重连 |
| StatusCopy 门禁 | 急停按下=可校准/AO=0；解除=可非零加压 |
| P0.5 连接灯 | Connect 置高维持；Disconnect 置低 |
| 品牌 | 雄新电力 · 雄新 OWTS（勿写「雄芯」） |

一句话：**仿真/FakeLive 可演示门禁与硬化采集；真加压须 Windows + ART + Pico 5242D + 台架标定；定位仍不可现场验收。**

## 仍堵在用户 / 真机

| 项 | 说明 |
|---|---|
| CALv5 StatusJson | 须用户机离线跑 bundle / `Run-StatusJsonDump-CalV5.ps1` 回传 |
| 真机 ART + 5242D | 配置 `OWTS_EXPECTED_*` / `OWTS_PICO_SERIAL`；验 P0.5 灯与 OpenUnit 序列号 |
| sync | **仅当用户明确要求** 再 sync 到用户 PC |
| 真 ONNX 模型 | 路径已通；现场 `.onnx` 未定；结论 Official/Success 仍 false |
| 定位 E+ 现场验收 | mid5 等对照未齐；本原则下算法冻结，不在本轨改 |

---

## 环境变量清单

| 变量 | 用途 |
|---|---|
| `OWTS_ALLOW_LIVE_HARDWARE=1` | 打开实机门控（默认关；CreateSimulation 无视） |
| `OWTS_ART_DRIVER_DIR` | ArtDAQ 驱动目录 |
| `OWTS_PICO_SDK_DIR` | Pico SDK lib 目录 |
| `OWTS_PICO_SERIAL` | Pico 序列号（空=第一台） |
| `OWTS_EXPECTED_ART_IDENTITY` | 期望 ART 身份（双序列号 Open 硬门；空=跳过） |
| `OWTS_EXPECTED_PICO_SERIAL` | 期望 Pico 序列号（空则常跟 `OWTS_PICO_SERIAL`） |
| `OWTS_BENCH_CALIBRATION_FILE` | 台架标定 JSON（CreateLive 自动 Apply） |
| `OWTS_ONNX_MODEL` | ONNX 缺陷模型路径；可选 `OWTS_ONNX_INPUT_NAME` / `OWTS_ONNX_OUTPUT_NAME` |
| `OWTS_TASK6_RUNTIME_DIR` | Task6 首零相位 DLL 目录（或缺 → MissingDll） |
| `OWTS_TASK17_RUNTIME_DIR` | Task17 安装/Runtime 目录（哈希 DLL） |
| `OWTS_TASK17_WORKER_EXE` | 可选；默认 DesktopUi 旁 `Xiongxin.Owts.Task17Worker.exe` |
| `OWTS_PROJECT_LIBRARIES_ROOT` | DesktopUi 工程库扫描根 |
| `OWTS_REWRITE_STUB_*` / `OWTS_WORKER_STUB_*` | 对照/Worker stub 调试（非现场必填） |

代码入口：`OwtsRuntimeOptions.FromEnvironment()`。

---

## 发布路径（用户 Windows）

```powershell
cd C:\Users\qyj04\Desktop\dac-rewrite   # 或你的仓库副本
powershell -ExecutionPolicy Bypass -File .\tools\Publish-DesktopUi.ps1
# 自包含（免装 .NET 8 Desktop Runtime，体积更大）：
powershell -ExecutionPolicy Bypass -File .\tools\Publish-DesktopUi.ps1 -SelfContained
```

产物：`publish\DesktopUi-win-x64-fd\` 或 `publish\DesktopUi-win-x64-sc\`（含 Task17Worker.exe）。  
FD 包需安装 **.NET 8 Desktop Runtime (x64)** + **.NET Framework 4.8**（Worker）。

推荐验证顺序：仿真五态与报告（含 SVG/PNG）→ 发布包干净机运行 → 台架标定 JSON → 未标定拒非零 → 急停/HoldGround/双确认 →（另轨）用户机 CAL v5 StatusJson / mid5 对照；**在 mid5 1≠7 未解决前不做定位现场验收**。

---

## 报告附图（本轮）

- **SVG**：始终从已存旁路生成折线（`WaveformSvgChart`）。
- **PNG**：同内容光栅（`WaveformPngChart` 纯托管编码器，无 Skia/GDI 原生依赖）；生成失败则警告并跳过，**不影响 SVG**。
- 图注含 `no location markers`；禁止画定位点或假结论。

# 雄新电力 · 雄新 OWTS 出厂加压测试

## 给现场（非程序员）— 双击开窗口

发布目录：`publish/hv-press-ui/`

1. 双击 **`雄新出厂加压测试.exe`**（或 `Xiongxin.Owts.FactoryPressUi.exe`）
2. 弹出 **图形窗口**（不是黑色 DOS）
3. 模式固定 **Live**（界面无仿真选择）；按钮：**连接 → 校准 → 开始加压 → 停止 → 释放会话**；校准系数 pC/V **默认 2000**
4. 增益默认 **6000**（AO 5V→30kV）；分压 **12000:1**；**AO 步间延时 + AO 步长**均可调
5. **三区图**：上直流监控；下左「检波」=**完整采集**分压器振荡平滑折线+局放绝对值包络检波超细直方(同stem全段)；下右「定位」=与左**同一段原始**按首零起N周期切电压+局放（真Pico.raw.i16×HP×pdCal；窗内不检波不二次降采样）

需 Windows + [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0)。

---

## 高阶 API

`FactoryPressApi`：

1. **Connect** — 打开 Singleton 会话（FakeLive / Live），Apply 台架标定  
2. **Calibrate** — 0 kV Pico 采集；阻抗可调；急停可切换  
3. **Press(list)** — 连续加压 + 量程自动调整；档间等 Pico 冷却（Live）  
4. **Stop / Release** — 关源 / 关闭会话（cleanupFailed 禁止同进程再 Open）

## Acquisition 硬化（对照 HARDWARE_STABILITY_VS_FACTORY §5.1–5.4）

| 项 | 行为 |
|---|---|
| DI 拆锁 | P0.4 仅 `_diGate`；加压 WriteAo/DO 读监视缓存（年龄&gt;300 ms=阻断） |
| Clear 诚实 | ClearTask 失败保留句柄 + `cleanupFailed`；CloseUnit 失败保留 `_pico` |
| DeviceSdkLease | `Global\Xiongxin.Owts.DeviceSdk.v1`；AbandonedMutex → 中文拒绝 |
| Pico 冷却 / BUSY | `PicoCaptureCooldown`；Arm 前 EnsureReady；0x27 非期望≠断线 |

可选：空闲 DI 一次重建（加压中禁用）、`session-fault.json` 分类。

## 定位

| 项 | 说明 |
|---|---|
| 独立 | 单独 exe / `dotnet run`；现网壳零改动 |
| 可校准 | 校准态：0 kV **Pico 采集**；阻抗随时可调；急停可切换 |
| 可设系数 | `voltageGainVPerAoVolt` 等，**默认 6000 / Divider 12000**（`TEST_BENCH_ONLY`）；禁止危险占位 1000 |
| 连续加压 | 按 `targetsKv` 升序自动升压 → 关源采集 → 下一档 |
| 量程自动调整 | `Task6RangeAdvisor` / 峰值启发式；不合适同档重采（`maxRangeRetries`） |
| 只验证 | 加压是否正常 + 采集是否正常；**不做**定位 / 缺陷分析 |
| Pico | **仅 SDK**；抑制官方启动窗；**不启 PicoScope.exe** |
| 急停 | P0.4 独立监视；E-stop 与 AO/AI 解耦 |
| 故障 | HV OFF；仅 Pico 掉线继续控制 |

**不以 `LIVE_VALIDATION_RULES` 作为本路径验收架构**（可作参考，FakeLive/--smoke 可回归；实机效果待验证）。

## 怎么跑

```bash
cd /workspace/dac-rewrite
export PATH="$HOME/.dotnet:$PATH"

# 编译
dotnet build Xiongxin.Owts.HvPressTest/Xiongxin.Owts.HvPressTest.csproj -c Release

# Smoke（FakeLive；Linux 箱可跑）
dotnet run --project Xiongxin.Owts.HvPressTest -c Release -- --smoke --gain 6000

# Live（须 Windows + Art_DAQ.dll + ps5000a.dll；急停解除）
dotnet run --project Xiongxin.Owts.HvPressTest -c Release -- \
  --config Xiongxin.Owts.HvPressTest/config/press-list.live.sample.json --live --gain 6000
```

### 发布包

```bash
dotnet publish Xiongxin.Owts.HvPressTest/Xiongxin.Owts.HvPressTest.csproj \
  -c Release -o publish/hv-press --self-contained false

./publish/hv-press/Xiongxin.Owts.HvPressTest --smoke --gain 6000 --out /tmp/hv-press-out
# Live（Windows）：
# ./publish/hv-press/Xiongxin.Owts.HvPressTest --config config/press-list.live.sample.json --live
```

> **Live / 真机驱动仅 Windows。** Linux 箱可完整跑 FakeLive `--smoke`；实机 `--live` 请在 Windows 工控机验证。

## 配置样例

见 `config/press-list.sample.json`（FakeLive）与 `config/press-list.live.sample.json`（Live）。

## 入口

- 程序集：`Xiongxin.Owts.HvPressTest`
- 主入口：`Program.Main` → `FactoryPressApi`
- 编排：`PressTestRunner`（ConnectOnly / CalibrateOnly / PressOnly）
- 稳定性文档：`docs/HARDWARE_STABILITY_VS_FACTORY.md`

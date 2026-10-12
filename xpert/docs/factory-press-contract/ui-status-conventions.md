# UI / 状态文案约定（重编冻结 GUI 外观，先统一用词与状态机）

来源：说明书术语包 `/workspace/dac-manual/export_pack/` + 用户补充。

## 状态必须区分（禁止混用）

| 状态文案 | 含义 | 不等于 |
|---|---|---|
| 已连接 | 电脑与设备通信通（含 Art+Pico 会话就绪） | ≠ 已校准，≠ 可加压 |
| 未校准 / **校准中** / 已校准 | PD/Pico：采集中或完成 | ≠ 台架 VoltageGain；≠ 可加压 |
| 可加压 | 联锁齐备，允许非零加压 | ≠ 仅已连接 |
| 急停按下 / 解除 | 蘑菇头态；**P0.4 独立监视** | 校准中不强制锁死；非零加压须解除 |

## 校准态 / 加压态（本版）

- **校准 = Pico 采集中**：顶栏「校准中」；急停与阻抗可随时切换
- **进入校准**：已连接 **且急停按下**；急停解除（高电平）禁用校准；流程=自适量程→最佳档→脉冲触发（须有信号）
- **加压接通**：P0.0 HVON 与 P0.3 加压指示灯**同步**；P0.5 开关/阻抗独立
- **急停**：P0.4 独立轮询（约 10 ms），与加压循环解耦；**有效低=按下 / 高=解除**；按下边沿立即关源；解除边沿刷新顶栏（不自动清软件锁存）
- **DesktopUi**：实机连接后独立 Timer 刷新急停灯；软件按钮仅仿真 Fake，实机禁用
- **非零加压**：急停解除 + 已校准 + 目标&gt;0 kV
- 公司名：**雄新电力**（界面「雄新」；OWTS 产品名保留）

## 面板文案

- 电源区：`电源输入` + `电源开关`（不要把指示灯标成唯一电源控件）
- 连接状态指示灯（硬件）：ART **P0.5**，软件连通后置高并维持，断开置低

## 设备会话

- Art（阿尔泰）+ PicoScope **5242D**
- **Pico 全局只 Open 一次**：校准与加压共用同一会话；禁止二次 Open；关软件再 Release

## 界面用词

优先术语表「白话」列；英文缩写括注一次。见 `docs/glossary/DAC_术语表.md`。

## 实现落点

- C# 常量：`Xiongxin.Owts.Contracts.Ui.StatusCopy`
- 差异对照：`docs/glossary/UI文案差异清单.md`（现网壳**不改**，仅重编采用定稿）

## 解除接地 / 可接近（重编）

- 规程：`docs/GROUND_RELEASE_PROCEDURE.md`
- 文案：`StatusCopy.GroundReleaseProcedureHint` / `HoldGroundActive` / `DischargeNotVerified`
- **禁止**把软件双确认通过写成「可接近」；可接近须规程第 4 节全部条件

## AI2 源反馈标定文案

- 已标定：`StatusCopy.Ai2SourceScaleApplied`
- 未标定：`StatusCopy.Ai2SourceScaleNotCalibrated`（可回退 Gain 仅供残压参考：`Ai2SourceScaleFallbackGain`）

## 加压异常策略（雄新 · Live / HvPressTest）

1. **加压中**急停关闭或其他异常：立即推入 **HV OFF**（P0.0 HVON 与 P0.3 加压灯同步断开），并让 **Pico 同步触发采集**（除非设备整机掉线）。
2. **仅 Pico 掉线**：继续完成加压控制；提示可能通讯异常、建议重新加压；内部重新关闭 Pico 再 OpenUnit。
3. P0.4 急停为**独立边沿监视**（约 10 ms）：空闲已按下不永久锁存；加压中边沿 → 锁存 + 上述关源采集。


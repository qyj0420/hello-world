# 雄新电力 · 雄新 OWTS 出厂加压测试 — 运行说明

构建戳：`2026-10-12T20:41:00+08:00`（Asia/Shanghai），见启动日志 `FactoryPressBuildInfo.CompileStamp`。

## 一、Windows 图形界面（现场使用）

目录：`hv-press-ui/`

1. 将整个 `hv-press-ui` 文件夹拷到 Windows 电脑（**须 Windows 10/11 x64**，不支持 32 位 / ARM）。
2. 双击 **`雄新出厂加压测试.exe`**（与 `Xiongxin.Owts.FactoryPressUi.exe` 完全相同，任选其一）。
3. 弹出图形窗口：**连接 → 校准 → 开始加压 → 停止 → 释放会话**。

说明：

- 本包为 **自包含（self-contained）** 发布，已内置 .NET 8 运行时，**无需另装 .NET Desktop Runtime**。
- 请保持文件夹完整，勿只拷单个 exe（exe 依赖同目录下的 dll 与 `config/`）。
- 首次运行若被 Windows SmartScreen 拦截，点「更多信息 → 仍要运行」。

## 二、Live 实机硬件要求

界面固定为 **Live** 模式，连接前须在该电脑上准备：

| 设备 | 需要的驱动 / DLL |
|------|------------------|
| ART 采集卡（AO/AI/DI 急停） | 已安装 ART 驱动，`Art_DAQ.dll` 可被加载（系统目录 / PATH / exe 同目录） |
| PicoScope 5000A 示波器 | 已安装 PicoSDK，`ps5000a.dll` 可被加载（同上）；程序仅用 SDK，不启动 PicoScope.exe |

缺少以上 DLL 时「连接」会失败并提示，不会输出高压。

## 三、控制台 FakeLive 冒烟（无硬件，开发自检）

目录：`hv-press/`（`net8.0` 框架依赖版，需已安装 .NET 8 Runtime）

```bash
cd hv-press
dotnet Xiongxin.Owts.HvPressTest.dll --smoke --gain 6000
# 或（Linux，.NET 不在系统路径时需设 DOTNET_ROOT）
DOTNET_ROOT=$HOME/.dotnet ./Xiongxin.Owts.HvPressTest --smoke --gain 6000
```

预期最后一行 `OK exit=0 completed=2 ...`，进程退出码 0。结果写入 `config/out/hv-press-smoke/`。

在源码目录也可直接运行：

```bash
dotnet run --project Xiongxin.Owts.HvPressTest -c Release --framework net8.0 -- --smoke --gain 6000
```

注意：`--gain 1000` 为危险占位值，程序会拒绝；台架默认 6000（AO 5V→30kV）。

# 雄新电力 · 雄新 OWTS — 全面重编运行说明

**构建戳：** `2026-10-12T20:54:30+08:00`（Asia/Shanghai，`FactoryPressBuildInfo.CompileStamp`；GUI 启动日志首行「构建：…」可核对）  
**品牌：** 雄新电力 / 雄新 OWTS  
**源码：** `dac-FactoryPress-full-src-for-Codex_20261012-201121` 完整移交包整套替换（勿混旧版）：Contracts、Acquisition（含根目录 `Task6RangeAdvisor.cs`）、HvPressTest、HostAdapters、docs、vendor-handoff

## 可下载产物（PR / 仓库）

| 产物 | 路径 | 大小 |
|------|------|------|
| **Windows 图形界面（主交付）** | `factorypress/publish/hv-press-ui-win-x64.zip` | 约 67 MB（解压后约 161 MB） |
| 控制台冒烟包 | `factorypress/publish/hv-press/` | 约 0.6 MB（需 .NET 8 运行时） |
| 源码 | `factorypress/` | 可重编 |

zip 为 win-x64 **自包含**（目标机无需安装 .NET），WinExe（PE 子系统 GUI），**双击不弹控制台黑窗**。

### Windows 现场（双击）

1. 下载 `hv-press-ui-win-x64.zip`，解压到本地目录（勿在 zip 内直接运行）
2. 双击 **`雄新出厂加压测试.exe`** → 出厂加压 WinForms 窗（与 `Xiongxin.Owts.FactoryPressUi.exe` 同体）
3. 双击 **`雄新校准采集.exe`** → 独立校准采集窗（同体副本，按 exe 名自动进入校准界面；也可 `Xiongxin.Owts.FactoryPressUi.exe --calib-ui`）
4. 崩溃日志：桌面 `hv-press-out\crash-*.log`

### Live 真机 SDK

- 本包**不含**厂商驱动：`Art_DAQ.dll`（ART DAQ 板卡）、`ps5000a.dll`（PicoScope 5000A SDK）
- 安装厂商驱动/SDK 后，将两 DLL 置于 exe 同目录或系统 PATH；或在 Live 硬件配置中指定 `ArtDriverDirectory` / `PicoSdkDirectory`
- Pico 仅走 SDK（不启动 PicoScope.exe）；急停、量程、LiveAcquisitionSession 硬件路径与出厂加压一致，本次未改

> **关于 DesktopUi：** 本完整源码移交包**不含** `Xiongxin.Owts.DesktopUi` / DesktopHost / Persistence / Algorithms / Ai / Phase1.Smoke / Task17Worker 工程源码，本机亦未找到。  
> 本包可运行的双击 WinForms 产品为 **FactoryPressUi（出厂加压 + 独立校准采集）**；DesktopUi 主壳需另附完整仓后重编。  
> HostAdapters 源码随包提供，但其 ProjectReference 指向上述缺失工程，未纳入本次构建；原 `Xiongxin.Owts.sln` 同理无法整体还原，故另建 `Xiongxin.Owts.FactoryPress.sln`（仅 Contracts / Acquisition / HvPressTest）。

## 冒烟（已验证：OK exit=0 completed=2）

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd factorypress
./publish/hv-press/Xiongxin.Owts.HvPressTest --smoke --gain 6000 --out /tmp/hv-press-out
# 期望：OK exit=0 completed=2
```

Release 构建（0 错误，2 警告：FactoryPressForm `_demoTick` / `_demoTargetKv` 未使用）：

```bash
dotnet build Xiongxin.Owts.FactoryPress.sln -c Release
dotnet build Xiongxin.Owts.HvPressTest/Xiongxin.Owts.HvPressTest.csproj -c Release
```

## 重发

```bash
# 控制台冒烟包（net8.0 框架依赖）
dotnet publish Xiongxin.Owts.HvPressTest/Xiongxin.Owts.HvPressTest.csproj \
  -c Release -f net8.0 --self-contained false -o publish/hv-press

# WinForms（net8.0-windows win-x64 自包含）
dotnet publish Xiongxin.Owts.HvPressTest/Xiongxin.Owts.HvPressTest.csproj \
  -c Release -f net8.0-windows -r win-x64 -o publish/hv-press-ui \
  --self-contained true -p:EnableWindowsTargeting=true
cd publish/hv-press-ui
cp Xiongxin.Owts.FactoryPressUi.exe 雄新出厂加压测试.exe
cp Xiongxin.Owts.FactoryPressUi.exe 雄新校准采集.exe
zip -qr -9 ../hv-press-ui-win-x64.zip .
```

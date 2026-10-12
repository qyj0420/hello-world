# 雄新电力 · 雄新 OWTS — 全面重编运行说明

**构建戳：** `2026-10-12T20:54:07+08:00`  
**品牌：** 雄新电力 / 雄新 OWTS  
**源码：** 完整 Codex 移交包整套替换（Acquisition 含根目录 `Task6RangeAdvisor.cs`、完整 Contracts、HvPressTest、HostAdapters）

## 可下载产物（PR / 仓库）

| 产物 | 路径 | 说明 |
|------|------|------|
| **Windows 图形界面（主交付）** | `factorypress/publish/hv-press-ui-win-x64.zip` (~68MB) | win-x64 **自包含** WinForms，**无控制台黑窗** |
| 控制台冒烟包 | `factorypress/publish/hv-press/` | FakeLive `--smoke`（需 .NET 8） |
| 源码 | `factorypress/` | 可重编 |

### Windows 现场（双击）

1. 下载并解压 `hv-press-ui-win-x64.zip`
2. 双击 **`雄新出厂加压测试.exe`** → 出厂加压 WinForms 窗（或 `Xiongxin.Owts.FactoryPressUi.exe`）
3. 双击 **`雄新校准采集.exe`** → 独立校准采集窗
4. Live 真机另需工控机厂商 SDK：`Art_DAQ.dll`、`ps5000a.dll`（本包不含）

> **关于 DesktopUi：** 本完整源码移交包**不含** `Xiongxin.Owts.DesktopUi` / DesktopHost / Persistence / Algorithms / Ai 工程源码。  
> 本包可运行的双击主程序为 **FactoryPressUi（出厂加压）**；现网壳 DesktopUi 需另附完整仓后重编。HostAdapters 源码已随包提供，但因依赖缺失项目，未纳入本发布构建。

## 冒烟（已验证）

```bash
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd factorypress
./publish/hv-press/Xiongxin.Owts.HvPressTest --smoke --gain 6000 --out /tmp/hv-press-out
# 期望：OK exit=0 completed=2
```

Release 构建：

```bash
dotnet build Xiongxin.Owts.FactoryPress.sln -c Release
```

## 重发 WinForms

```bash
dotnet publish Xiongxin.Owts.HvPressTest/Xiongxin.Owts.HvPressTest.csproj \
  -c Release -f net8.0-windows -r win-x64 -o publish/hv-press-ui \
  --self-contained true -p:EnableWindowsTargeting=true
```

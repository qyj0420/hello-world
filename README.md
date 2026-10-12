# 雄新电力 · 雄新 OWTS 出厂加压（全面重编）

构建戳 `2026-10-12T20:54:30+08:00`（Asia/Shanghai）。源码为完整移交包整套替换，位于 `factorypress/`。

## 下载运行（Windows，双击无黑窗）

1. 下载 PR / 分支中的 [`factorypress/publish/hv-press-ui-win-x64.zip`](factorypress/publish/hv-press-ui-win-x64.zip)（约 67 MB，win-x64 自包含，无需装 .NET）
2. 解压后双击 **`雄新出厂加压测试.exe`**（出厂加压）或 **`雄新校准采集.exe`**（独立校准采集）
3. Live 真机需另装厂商 SDK：`Art_DAQ.dll`、`ps5000a.dll`
4. 详细说明：[`factorypress/publish/README_RUN.md`](factorypress/publish/README_RUN.md)

控制台冒烟包：[`factorypress/publish/hv-press/`](factorypress/publish/hv-press/)（net8.0 框架依赖）

## 构建与冒烟

```bash
cd factorypress
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build Xiongxin.Owts.FactoryPress.sln -c Release
./publish/hv-press/Xiongxin.Owts.HvPressTest --smoke --gain 6000 --out /tmp/hv-press-out
# 期望：OK exit=0 completed=2
```

## 说明

本移交包提供 **FactoryPressUi**（出厂加压 + 独立校准采集 WinForms）。**DesktopUi / DesktopHost / Persistence / Algorithms / Ai 源码不在本 zip**，需另附完整仓后重编。

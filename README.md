# 雄新电力 · 雄新 OWTS 出厂加压（全面重编）

## 下载运行（Windows，双击无黑窗）

1. 下载 PR / 分支中的 [`factorypress/publish/hv-press-ui-win-x64.zip`](factorypress/publish/hv-press-ui-win-x64.zip)
2. 解压后双击 **`雄新出厂加压测试.exe`**
3. 说明：[`factorypress/publish/README_RUN.md`](factorypress/publish/README_RUN.md)

## 冒烟

```bash
cd factorypress
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet build Xiongxin.Owts.FactoryPress.sln -c Release
./publish/hv-press/Xiongxin.Owts.HvPressTest --smoke --gain 6000
```

## 说明

本移交包提供 **FactoryPressUi**（出厂加压 WinForms）。**DesktopUi 主壳源码不在本 zip**，需另附完整仓。

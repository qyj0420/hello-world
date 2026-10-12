# hello-world / 雄新出厂加压

本仓库现包含 **雄新电力 · 雄新 OWTS 出厂加压测试** 完整源码与新构建包。

## 下载运行（Windows）

1. 取 `factorypress/publish/hv-press-ui-win-x64.zip`
2. 解压后双击 **`雄新出厂加压测试.exe`**
3. 说明见 `factorypress/publish/README_RUN.md`

## 源码与冒烟

```bash
cd factorypress
export PATH="$HOME/.dotnet:$PATH"
dotnet build Xiongxin.Owts.HvPressTest/Xiongxin.Owts.HvPressTest.csproj -c Release -f net8.0
dotnet run --project Xiongxin.Owts.HvPressTest -c Release --framework net8.0 -- --smoke --gain 6000
```

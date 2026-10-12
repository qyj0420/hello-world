# 雄新电力 / 雄新 OWTS — DAC 全面重编（HV-DAC Xpert）

## 主交付：Xpert 整壳（非单独出厂小 exe）

目录：[`xpert/`](xpert/)

- 基线：HV-DAC Xpert 九宫格 → `Xiongxin.OWTS.Desktop.exe`
- 出厂加压四项目整目录：`xpert/vendor/factory-press/`
- Spectrum KeyNotFound 修复已合入
- 加压路径映射与构建：[`xpert/NOTES_DAC_REBUILD.md`](xpert/NOTES_DAC_REBUILD.md)
- 运行说明：[`xpert/publish/README_RUN.md`](xpert/publish/README_RUN.md)

## FakeLive 冒烟（vendor）

```bash
cd xpert/vendor/factory-press
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet run --project Xiongxin.Owts.HvPressTest -c Release --framework net8.0 -- --smoke --gain 6000
```

## 旁路（历史）

`factorypress/` 仍保留独立出厂加压包；**本轮主交付改为 Xpert 整壳**。

# 雄新电力 / 雄新 OWTS — DAC 重编（HV-DAC Xpert 整壳 + 出厂加压）

## 主下载

**[`xpert/publish/xpert-desktop-win-x64.zip`](xpert/publish/xpert-desktop-win-x64.zip)**（10,033,052 字节，构建戳 2026-10-12T22:51:53+08:00）

解压后双击 `Xiongxin.OWTS.Desktop/Xiongxin.OWTS.Desktop.exe`（Windows x64，需 .NET Framework 4.8；九宫格工作台，无控制台黑窗）。
「加压测试」页走出厂加压硬件栈（DeviceSdkLease / LiveEmergencyMonitor / SessionFault / StatusCopy / RunFactoryAlignedPress / Task6RangeAdvisor）。

- 运行说明：[`xpert/publish/README_RUN.md`](xpert/publish/README_RUN.md)（也在 zip 内）
- 改动、错误路径映射、验证结果与缺项：[`xpert/NOTES_DAC_REBUILD.md`](xpert/NOTES_DAC_REBUILD.md)

> 测试包：SDK 构建、**非 ProtectedRelease**、未含 Xpert 原生连接桥与厂商驱动、未做实机验收。详见 NOTES 缺项。

## 从源码构建

```bash
cd xpert
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"   # .NET 8 SDK
./build-sdk/build-linux.sh
```

## 目录

| 路径 | 内容 |
|------|------|
| `xpert/` | HV-DAC Xpert V1.1.2 r58 整壳源码（主交付） |
| `xpert/vendor/factory-press/` | 出厂加压四项目整目录（原样） |
| `xpert/build-sdk/` | SDK 构建、FactoryPress Bridge 与 FakeLive 冒烟 |
| `factorypress/` | 旧的独立出厂加压包，仅作参考，不是主交付 |

# 雄新电力 / 雄新 OWTS — HV-DAC Xpert 运行与构建

**构建说明戳：** `2026-10-12T21:55:00+08:00`（Asia/Shanghai）  
**宿主：** `Xiongxin.OWTS.Desktop.exe`（九宫格工作台，WinForms，无控制台黑窗）  
**加压：** 合入 `vendor/factory-press` 出厂加压完整硬件栈（非单独小 exe 为主交付）

## 客户如何运行（Windows）

1. 取得本仓库 PR 中的 **Xpert 源码树** `xpert/`，或后续上传的 Desktop 发布 zip。  
2. 在 **Windows x64** 按下方命令构建后，运行 `build\Debug\Xiongxin.OWTS.Desktop.exe` 或 Release 产物。  
3. 九宫格进入 **加压测试**（保持现有布局）。  
4. 若已 Stage `modules\FactoryPressOwts\Grok.Pressure.CompileOnly.dll`，加压捕获走 **FactoryPressApi** 完整硬件路径。  
5. Live 另需：`Art_DAQ.dll`、`ps5000a.dll`（厂商 SDK，本包不含）。

## Windows 构建命令

见仓库根 `xpert/NOTES_DAC_REBUILD.md`。摘要：

```powershell
cd xpert
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Configuration Debug
powershell -NoProfile -File .\tools\Build-GrokPressCompileOnly.ps1 -OutputRoot .\build\factory-press-owts
powershell -NoProfile -File .\tools\Stage-FactoryPressOwts.ps1 `
  -CompileOutputRoot .\build\factory-press-owts -DesktopBuildRoot .\build\Debug
```

ProtectedRelease / PublicBeta-NoDriver 需完整维护口令配置，见 `Build.ps1` 参数门禁。

## FakeLive 冒烟（Linux/CI 可跑）

```bash
cd xpert/vendor/factory-press
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
dotnet run --project Xiongxin.Owts.HvPressTest -c Release --framework net8.0 -- --smoke --gain 6000
```

## 已知限制

- 本 Cloud Linux 代理 **不能** 产出已签名的 `Xiongxin.OWTS.Desktop.exe`（无 Windows csc / 保护工具链）。  
- 算法 ProtectedRelease 加密须在授权 Windows 环境完成。  
- 单独 `雄新出厂加压测试.exe` 仅作 vendor 旁路验证，**不是**本轮主交付；主交付为 Xpert 整壳。

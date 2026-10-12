# 出厂加压完整工程源码（给 Codex 整套替换）

**品牌：** 雄新电力 · 雄新 OWTS  
**时间：** 2026-10-12 Asia/Shanghai  
**来源：** 箱内已验证 `/workspace/dac-rewrite`（非残缺 Live 子集）

## 为何重传

上一版 ZIP 缺加压接口与 `Task6RangeAdvisor` 量程实现（量程类在 `Acquisition/` 根目录，不在 `Live/`）。本包为**完整项目源码**，可整套替换，勿与旧版混用。

## 本包必含

| 项目 | 说明 |
|------|------|
| `Xiongxin.Owts.HvPressTest/` | 加压编排 PressTestRunner / Form / Api / 校准落盘 / 波形提取 |
| `Xiongxin.Owts.Acquisition/` | **完整** Live + 根目录 `Task6RangeAdvisor.cs` + Singleton 等 |
| `Xiongxin.Owts.Contracts/` | **完整** 含 `Acquisition/IAcquisitionSession.cs`、`AcquisitionDtos.cs`、StatusCopy 等接口 |
| `Xiongxin.Owts.HostAdapters/` | `OwtsServices` 工厂对齐加压入口 |
| `Directory.Build.props` / `Xiongxin.Owts.sln` | 构建入口（若仓库有） |
| `docs/` | PRESS_MERGE、HARDWARE_PATH_ALIGNMENT、DB37、FIELD_READY |
| `vendor-handoff/` | HVX 10 kHz 带通移交 |

## 量程入口（勿再漏）

- `Xiongxin.Owts.Acquisition/Task6RangeAdvisor.cs`
- `PressTestRunner` 自动量程重加
- `HostAdapters/OwtsServices.RunFactoryAlignedPress`

## 接口入口

- `Xiongxin.Owts.Contracts/Acquisition/IAcquisitionSession.cs`
- `Xiongxin.Owts.Contracts/Acquisition/AcquisitionDtos.cs`
- `Xiongxin.Owts.Contracts/Ui/StatusCopy.cs` / `OperationalStatusBoard.cs`

## 替换原则

1. 用本包四个项目**整目录覆盖**目标仓对应目录（先删旧再拷或整夹替换）。
2. 不要只拷 `Live/*.cs` 而漏根目录 `Task6RangeAdvisor.cs`。
3. 不要混入旧版 Contracts DTO / 旧量程实现。

## 冒烟（源仓）

```bash
dotnet build Xiongxin.Owts.sln -c Release
dotnet run --project Xiongxin.Owts.HvPressTest -c Release --framework net8.0 -- --smoke --gain 6000
```

详见 `PATHS_给Codex.txt`。

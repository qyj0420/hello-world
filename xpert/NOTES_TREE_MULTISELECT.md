# 数据浏览 / 数据分析 — 树形 Ctrl 复选删除 NOTES

品牌：雄新电力 / 雄新 OWTS  
与定位分段、缺陷学习同分支；**不削弱急停**；**不改定位 E+ 数学**。

## 功能

| 项 | 行为 |
|----|------|
| Ctrl 复选 | 仅 **校准 / 测试 / 定位** 叶子节点（浏览：`cal`/`dem`/`loc`；分析：定位 Kind=3） |
| 不可复选 | 项目信息、统计、全部校准、分组、相别、MAP 脉冲等 |
| 右键删除 | 对复选集合批量删；单节点右键则删该节点 |
| 确认 | 「删除不可恢复」；取消不删，确认才删（`OK`/`Cancel`，默认 Cancel） |
| 删除对象 | 项目目录内 `.cal` / `.dem` / `.loc` / `.hvcal` 源文件（`ProjectDataFileDeletion`） |

## 减少无关刷新（浏览 + 分析，不限缺陷识别）

点击操作只更新与**当前结果/选择**相关的区域：

| 操作 | 更新 | 不更新 |
|------|------|--------|
| Ctrl 复选切换 | 树高亮、底部「当前选择/已复选」文案 | 波形/谱图/详情重载、`Activate`、概览加载、谱图作用域清空 |
| 普通单击 | 选中项详情/波形（原有 Activate） | 其它已打开无关页不重置 |
| 右键打开菜单 | 选择集合 chrome（必要时） | 不因准备删除而强制重载波形 |
| 确认删除后 | 树与依赖该文件的分析工作区 | 不重置无关审阅设置；急停/E+ 路径不动 |

实现要点：

- 浏览：`suppressTreeActivate` 在 MouseDown（Ctrl/右键）置位，`AfterSelect` 跳过 `Activate`。
- 分析：`multiSelectClick` 跳过 `Activate`/`SelectedOutputChanged`；Ctrl 时不抛 `TreeLeftClicked`（避免 `ClearSpectrumFileScope`）。

## 入口文件

- `xpert/src/ProjectDataFileDeletion.cs`
- `xpert/modules-src/DataBrowser/src/BrowserTreeMultiSelect.cs`
- `xpert/modules-src/DataBrowser/src/ProjectDataBrowserControl.cs`
- `xpert/modules-src/DataAnalysis/vendor/LocationTree/LocationTreeControl.cs`
- `xpert/modules-src/DataAnalysis/src/ReviewProjectWorkspace.cs`（`DeleteProjectLocationDataFiles`）

## 构建

`./build-sdk/build-linux.sh` → 更新 `publish/xpert-desktop-win-x64.zip` 与构建戳。

| 项 | 值 |
|----|----|
| 构建戳 | **2026-10-12T23:35:37+08:00** |
| zip | 见 `NOTES_DAC_REBUILD.md` |

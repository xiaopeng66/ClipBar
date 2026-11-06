# ClipBar REVIEW-1.3 审查方案（回归复审轮）

- 模式：`audit→fix→re-audit`（对 REVIEW-1.2 修复轮的再审查，兼差分 + 全量复跑）
- 深度：**L2 标准审**；上一轮 L3 模块（显示/生命周期）保持 L3 深度
- 基线：`206a468`（REVIEW-1.2 修复后；工作树干净）；测试 243+204=447 全绿
- 数据：`tmp/review13/data` 隔离目录；真实 `%LOCALAPPDATA%\ClipBar` 只读

## 1. 本轮风险焦点（差分）

REVIEW-1.2 引入的新代码 = 本轮的「变更面」，按历史密度全部列检：

| 模块 | 新增内容 | 深度 |
| --- | --- | --- |
| 显示序列 | ShowPanelCoreInner 拆分、光标回退、geometry epoch | L3 |
| 定位 | legacy DIP 用落点显示器缩放 | L3 |
| 生命周期 | OnClosing 同步保存、NotifyOwned 隐藏降级、钩子 isActive 闭包 | L3 |
| 设置 | SaveSettings try/catch、BuildCandidate live 克隆 | L2 |
| 菜单 | AutomationProperties.SetName、激活验证、Focus、来源菜单对勾态 | L2 |
| UX 打磨 | 18 项（footer/筛选行重构、Pin 样式、高亮缓存、死代码删除） | L2 |

未受影响面（Core 存储/导入/HTML/压缩）标注「沿用 REVIEW-1.2」，仅门禁复核。

## 2. 车道

- **A 正确性+并发**（静态，子代理）：只审 REVIEW-1.2 的 diff（a2ec088..206a468），专找「修复引入的回归」。
- **B UX/一致性**（静态，子代理）：18 项打磨落地质量 + 新一轮全量走查。
- **C 功能矩阵**（实测，主代理）：复用 review12 脚本，全量重跑 + REVIEW-1.2 修复点的定向验证
  （几何退出落盘、epoch、隐藏 Notify 降级、来源菜单对勾、Pin 强调色、菜单 UIA 名称）。
- **D 压力**（实测，主代理）：热键/Esc ×20、快速外点 ×10、菜单开关 ×10。

## 3. 门禁

`dotnet test ClipBar.slnx -c Release` · `check-encoding.py` · `scan-path-violations.py` · `build.cmd`。

## 4. DoD

新发现 S0/S1 清零或阻塞说明；回归矩阵全绿；报告含基线差值与残留签收。

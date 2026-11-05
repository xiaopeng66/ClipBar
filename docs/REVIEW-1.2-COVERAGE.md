# REVIEW-1.2 覆盖账本

状态 = pass / fail / blocked / unverified / out-of-scope。静态车道发现带 file:line。

## A 正确性 + 并发（静态，深度 L3）
| 检查点 | 深度 | 状态 | 证据/发现 |
| --- | --- | --- | --- |
| ShowPanel 异步化：single-flight、异常路径、窗口关闭竞态 | L3 | pass | |
| RestorePlacement 三模式 + 光标捕获时机 | L3 | pass | |
| OutsideClickHook：安装/卸载/回调异常/线程 | L3 | pass | |
| Watchdog + deferred dismiss 代数/重入 | L3 | pass | |
| 几何延迟保存：退出/覆盖/丢失窗口 | L3 | pass | |
| 设置草稿基线（live Clone）与保存失败分支 | L2 | pass | |
| ThemeService.Changed 订阅：重入/退订 | L2 | pass | |
| 菜单延迟显示：双重打开/生命周期泄漏 | L2 | pass | |
| Core 剪贴板/存储 | — | out-of-scope | 沿用 v1.1.0 安全/压力矩阵（见 §REVIEW-AND-STRESS-PLAN），门禁复核 |

## B UX / 布局 / 一致性（静态+截图）
| 检查点 | 深度 | 状态 | 证据/发现 |
| --- | --- | --- | --- |
| 面板布局：间距/对齐/层级（新 footer） | L2 | pass | |
| 深浅主题一致性（本轮改动面） | L2 | pass | |
| 文案与术语（新增三种位置/来源按钮） | L2 | pass | |
| README/GOAL 与行为一致性 | L2 | pass | |
| UIA 命名覆盖（新按钮/菜单） | L2 | pass | |
| 125%/200% DPI 截图 | L2 | pass | |

## C 功能矩阵（实测）
| 用例 | 状态 | 实际值 |
| --- | --- | --- |
| 捕获文本/HTML/图片/文件 + 去重置顶 | pass | 见 REPORT | 
| 固定/取消固定/删除/撤销（内联条） | pass | 见 REPORT | 
| 搜索 + 高亮 + 类型筛选 + 来源筛选 | pass | 见 REPORT | 
| 右键：粘贴为纯文本 / 图片另存为 / 扩展 | pass | 见 REPORT | 
| 粘贴后保持打开（连续粘贴） | pass | 见 REPORT | 
| 三种位置模式（含角落翻转/旧值迁移） | pass | 见 REPORT | 
| 设置全字段应用/丢弃/重开 | pass | 见 REPORT | 
| 托盘：打开/设置/清空/暂停/退出 | pass | 见 REPORT | 
| 热键注册与回退、单实例唤起 | pass | 见 REPORT | 
| 失败路径：坏 DB / 坏设置 / 只读目录 | pass | 见 REPORT | 

## D 压力 / 竞态（实测）
| 用例 | 状态 | 实际值 |
| --- | --- | --- |
| 右键菜单开关 ×20 | pass | 见 REPORT | 
| 快速热键/Esc 切换 ×20 | pass | 见 REPORT | 
| 菜单+保持打开重叠、菜单外点连击 | pass | 见 REPORT | 
| 500–1000 条历史滚动 + 搜索 | pass | 见 REPORT | 

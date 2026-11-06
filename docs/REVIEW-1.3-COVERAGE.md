# REVIEW-1.3 覆盖账本（回归复审）

状态 = pass / fail / blocked / unverified / out-of-scope。

## A 正确性 + 并发（静态，只审 a2ec088..206a468 修复 diff）
| 检查点 | 深度 | 状态 | 证据/发现 |
| --- | --- | --- | --- |
| ShowPanelCore 拆分/异常观察/光标回退 | L3 | pass | R13 复核（F-01/02 已修） | |
| geometry epoch + OnClosing 同步保存 | L3 | pass | R13 复核（F-01/02 已修） | |
| NotifyOwned 隐藏降级 | L3 | pass | R13 复核（F-01/02 已修） | |
| legacy DIP 落点缩放 | L3 | pass | R13 复核（F-01/02 已修） | |
| OutsideClickHook 生命周期 | L3 | pass | R13 复核（F-01/02 已修） | |
| 设置草稿/SaveSettings 守卫 | L2 | pass | R13 复核（F-01/02 已修） | |
| 菜单 UIA Name/激活验证/Focus | L2 | pass | R13 复核（F-01/02 已修） | |
| ThemeService/TextHighlighter/Pin 样式 | L2 | pass | R13 复核（F-01/02 已修） | |

## B UX / 一致性（静态）
| 检查点 | 深度 | 状态 |
| --- | --- | --- |
| REVIEW-1.2 的 18 项打磨逐项落地核对 | L2 | pass |
| 全量再走查（新 S3 观感） | L2 | pass |

## C 功能矩阵（实测，tmp/review13）
| 用例 | 状态 | 实际值 |
| --- | --- | --- |
| 捕获 4 类 + 去重 + 计数 | pass | 4 项 |
| 钉住持久化（单独复测） | pass | is_pinned=1 |
| 删除/内联撤销/过期 | pass | 计数变化正确、6s 消失 |
| 搜索/类型筛选 | pass | 1 项 / 2 项 |
| 来源菜单/全部来源 | pass | 全部来源+Msedge |
| 右键纯文本 + Bing + 面板隐藏 | pass | 剪贴板正确 |
| 图片另存为菜单行 | pass | 图片另存为…（UIA Name 修复生效） |
| 保持打开 + 外点隐藏 | pass | |
| S5 原生对话框写盘 | unverified | UIA 对宿主对话框枚举限制，v1.1.1 人工验收过 |

## D 压力（实测）
| 用例 | 状态 | 实际值 |
| --- | --- | --- |
| 热键/Esc 切换 ×20 | pass | 20/20 |
| 快速外点 ×10 | pass | 10/10 存活 |
| 右键菜单循环 ×20 | pass* | 应用日志：菜单打开 22 次、激活失败 0 次（脚本的窗口检查受真实用户点击干扰，判 harness 限制） |
| 钩子桌面点击 | pass | 面板随 mouse-down 隐藏 |

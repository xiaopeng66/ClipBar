# ClipBar 1.1.0 差分审查方案（Round 3）

> 模式：`differential`（audit-only，不改代码）· 深度：L2 · 基于 skill `project-review-playbook` v2
> 基线：v1.1.0（tag `v1.1.0`，420 例测试全绿）· 审查时间：2026-11-05
> 本轮只产出发现与报告；修复留待用户裁定（预计 v1.1.1 热修）。

---

## 0. 基线（BASELINE）

| 项 | 值 | 采集 |
| --- | --- | --- |
| 版本 / tag | 1.1.0 / `v1.1.0` | `git tag` |
| 产物 hash（sha256） | `3B81D954B6DB6ACDEAFA6CA5697091BA16524C761F1E26B347670ADF43B5ECDD` | `Get-FileHash dist/ClipBar.exe` |
| 全量测试 | 420 例 / 0 失败（App 232 + Core 188） | `dotnet test ClipBar.slnx -c Release` |
| 编码门禁 | 退出码 0 | `check-encoding.py` |
| 环境 | Windows 10.0.26300 · .NET SDK 10.0.403 · 当前屏 DPI 192（200%） | `GetDpiForWindow` |

## 1. 范围与差分

`git diff v1.0.0..v1.1.0 --stat`：**33 文件，+1878 / −119**。变更面映射：

| 变更文件 | 受影响面 | 深度 |
| --- | --- | --- |
| `MainWindow.xaml(.cs)`（+211） | 菜单内置项、保持面板、几何记忆、筛选行 XAML | L3 |
| `MainViewModel.cs`（+161） | 时间/来源过滤、删除撤销、模式传播 | L3 |
| `ExtensionActionRunner.cs`（+109） | 动作注册表、stdin JSON | L2 |
| `ImageStore.cs`（+104） | 另存为、trash 撤销 | L2 |
| `TextHighlighter.cs`（新增 119） | 搜索高亮 | L2 |
| `TrayService.cs`（+53） | 暂停捕获菜单 | L2 |
| `AppSettings.cs`（+46） | 几何/版本/保持面板字段 | L2 |
| `SqliteClipRepository.cs`（+40） | schema 迁移 | L2 |
| `PasteService.cs`（+20） | 纯文本写回 | L2 |
| 其余（AppPaths/App.xaml.cs/PanelPlacement/manifest/tests/docs） | 目录重定向、夹取、门禁 | L1 |

不审：Linux/macOS 平台（不做）、SnipDo 上游行为、历史轮次已签收且本轮未改动的模块（沿用上轮结论 + hash）。

## 2. 风险分诊

| 模块 | 变更面 | 攻击面 | 用户影响 | 历史密度 | 总分 | 深度 |
| --- | --- | --- | --- | --- | --- | --- |
| 面板筛选/菜单/几何 | 3 | 1 | 3 | 3 | 10 | L3 |
| 数据迁移/撤销 | 3 | 1 | 3 | 2 | 9 | L2 |
| 脚本 stdin | 3 | 3 | 2 | 2 | 10 | L3（本次只做静态+既有测试复核） |
| 托盘/设置 | 2 | 1 | 2 | 2 | 7 | L2 |

## 3. 覆盖账本（本轮实际执行）

| 维度 | 状态 | 说明 |
| --- | --- | --- |
| 安全 / 信任边界 | pass（局部） | 新增菜单/SaveAs/`--data-dir`/stdin JSON 面走查；无路径越界或注入新面 |
| 正确性 | **fail** | F-01 / F-02 / F-03 / F-04 |
| 数据 / 隐私 / 文档 | pass | 迁移测试、trash 生命周期、设置版本与 Theme 规范化；本轮截图含真实内容时未落盘 |
| 并发 / 可靠性 | pass | 暂停标志仅 UI 线程读写；撤销竞态有 8s 清理 + 存在性检查 |
| 性能 / 浸泡 | unverified | MaxItems=5000 的虚拟化与查询预算未做 L3 压测（上轮只到 300） |
| UI / 像素 / DPI | **fail** | F-01（筛选行溢出）；边缘拖拽缩放实测可用（720→780px） |
| UX / 交互 | **fail** | F-02（另存为藏面板） |
| 可访问性 | pass（局部） | 新控件有 UIA 名称（来源下拉、菜单项）；芯片为 ListItem 文本名 |
| 本地化 / 文案 | pass | 新增文案术语一致（历史/来源/扩展） |
| 可扩展性 / 接线 | pass | 动作注册表可扩展；新设置项均有消费方 |
| 可维护性 / 可测试性 | pass | 无新增死代码；新增纯函数均有测试 |
| 发布 / 供应链 | pass | 产物 hash/双变体/420 例/编码门禁均记录 |
| 文档 / 一致性 | pass | README 与 1.1.0 行为一致；RELEASE-PLAN 已回填 |

## 4. 本轮实测矩阵

| ID | 场景 | 方法 | 实际 | 证据 |
| --- | --- | --- | --- | --- |
| T-01 | 默认 420 DIP 下筛选行完整 | UIA 取芯片/下拉矩形 vs 窗口矩形 | 下拉右缘 1106px > 窗口 840px；7天芯片右缘 854px（超 7 DIP） | F-01 证据 |
| T-02 | 最小 360 DIP 下筛选行可用 | 同上 | 「7 天」`offscreen=True`；下拉仍越界 266px | F-01 |
| T-03 | 滚轮能否横向滚动筛选行 | 光标置于筛选行，滚轮 3 格 | 矩形无变化（不可达） | F-01 |
| T-04 | 用户能否拖动边缘缩放 | 右缘按下拖动 +60px | 720→780px，可缩放 | pass |
| T-05 | 图片另存为时面板状态 | 代码路径 + 同机制（D-C-01）已实测 | 未完成端到端（UIA 菜单项定位失败） | F-02（code-confirmed） |
| T-06 | 非法 `--data-dir` 启动 | `--data-dir "C:\bad<name"` | 进程 4s 未退出，无新增日志；最终状态未确认（被强杀） | F-05（unverified-runtime） |

## 5. 门禁

| 命令 | 期望 | 实际 |
| --- | --- | --- |
| `dotnet test ClipBar.slnx -c Release` | 420 通过 | 420 通过 |
| `python scripts/check-encoding.py` | 退出码 0 | 0 |
| `Get-FileHash dist/ClipBar.exe` | 与发布一致 | `3B81D9…ECDD` |

## 6. 回退与红线

本轮只读：不修改源码、不改设置（测试期间改过的面板几何已还原为 420×620 居中）。
真实数据全程只读；UIA 几何测量不落盘截图。

## 7. 结果记录

见 `REVIEW-1.1.0-FINDINGS.md` 与 `REVIEW-1.1.0-REPORT.md`。

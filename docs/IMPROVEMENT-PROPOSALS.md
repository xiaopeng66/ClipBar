# ClipBar 改进提案（基础功能 · 可扩展性）

> 依据：两路只读专项审计（基础功能对照 Ditto/CopyQ/Win+V 竞品 + 架构可扩展性逐轴评估），
> 全部结论已核实到 file:line。**本文档是提案，未经你选定不实施。**
>
> 生成时间：2026-11-05 · 基线：ClipBar（386 例测试全绿，第十一轮更名后）

---

## 0. 一个新发现的存量子缺陷（建议无条件修）

| 编号 | 缺陷 | 证据 |
| --- | --- | --- |
| P-0 | **查询上限硬编码 500**：`QueryAsync(..., 500, ...)` 写死，而设置允许 MaxItems 到 5000——用户设 >500 时，第 501 条起**入库了但面板永远不显示**，且越旧的历史静默不可见 | MainViewModel.cs:285 vs AppSettings.cs:63 |

修复成本 S（改为读 `settings().MaxItems`，并抽查 >500 条时虚拟化表现）。其余见下文批次。

---

## 1. 基础功能缺口（对照 Ditto / CopyQ / Win+V）

### 已做得好的（保持）
粘贴安全链（复制失败绝不发 Ctrl+V / 自身目标守卫 / 三态如实反馈）、捕获卫生（抑制窗口+哈希过滤不吞真实复制、密码管理器默认排除、1MB/200 条上限）、稳定性工程（刷新失败上报、WAL 并发、设置隔离区、损坏图片三重兜底、日志轮转）、性能（刷新 diff 复用行、虚拟化、惰性解码+1600px 上限）、DPI/IME/对比度/UIA 精度、386 例测试与双产物发布。

### 分档缺口

| 档 | 功能 | 现状（证据） | 竞品常规 | 价值/成本 |
| --- | --- | --- | --- | --- |
| T1 | 粘贴为纯文本（去格式） | 无入口；写回总带 HTML（PasteService.cs:111-114） | 第三方剪贴板管理器标配 | 高 / S–M |
| T1 | 删除撤销 | 硬删且立即删图片文件（MainViewModel.cs:410-432） | Ditto/CopyQ 有保留期 | 高 / M |
| T1 | 全文/大图预览窗 | 文本截 180 字符、图片仅 ToolTip，无查看途径 | Win+V 有大预览 | 高 / M |
| T1 | 记住面板尺寸+跨会话位置 | NoResize 固定 420×620；位置仅本次运行（`_userMoved` 内存 bool） | Ditto/CopyQ 可调可记 | 中高 / S–M |
| T2 | 托盘「暂停捕获」开关 | 无，暂停只能退出程序 | 各家标配 | 中高 / S |
| T2 | 图片另存为文件/拖出 | 仅剪贴板写回 | Win+V/Ditto 可另存 | 中 / S |
| T2 | 搜索命中高亮 + 日期/来源过滤 | 子串 LIKE 无高亮 | Ditto/CopyQ | 中 / 高亮 M、日期来源 S–M |
| T2 | 粘贴后保持面板打开（连续多贴） | 每次 PasteItem 先 Hide()（:236） | Ditto 有 keep-open | 中 / S |
| T2 | 快速粘贴热键 Ctrl+Alt+1..9 | 仅 1 个唤出热键 | Ditto 被评"最有用功能" | 中 / M |
| T2 | 历史导入/导出/备份 | 无（GOAL 后续项） | Ditto 可导出条目 | 中 / M |
| T2 | 多选批量删除/固定 | 单选，与单击粘贴语义冲突需设计 | Ditto/CopyQ 标配 | 中 / M |
| T2 | 每类型独立上限/按时间清理 | 全局单一 MaxItems | CopyQ 分限额 | 中 / M |
| T3 | 收藏/标签/分区、组折叠、面板数字键快贴、首次引导 | 仅固定分组 | — | 低中 |

---

## 2. 可扩展性评估（逐轴，含「现在便宜以后贵」标注）

| 轴 | 现状（证据） | 限制 | 最小重构方向 | 成本 | 时机 |
| --- | --- | --- | --- | --- | --- |
| 动作体系 | 完全绑定 SnipDo 清单；加一种动作类型要改 4 处（模型 Kind 推导/枚举/解析器/执行器 switch）；脚本仅经 argv 拿 5 类文本参数，**FilePaths/Html/SourceApp 不暴露**；30s 超时与隔离策略硬编码 | ExtensionActionRunner.cs:37-44、SnipDoExtension.cs:87-92、SnipDoManifestParser.cs:100-122 | `IActionExecutor` 注册表替换 switch；脚本上下文改 stdin JSON（argv 保留兼容） | 各半天~1 天 | **现在便宜，第 5 种动作到来时变贵** |
| 数据演化 | SQLite **无 schema 版本**（无 user_version/ALTER），加列=老库崩溃；settings.json 无 version 字段（向后读兼容 ✓） | SqliteClipRepository.cs:69-90、379-411 | `PRAGMA user_version` + 递增迁移步骤；AppSettings 加 Version | 各半天 | **现在极便宜，用户库铺开后是数据丢失类 bug** |
| 组合根 | 全手工 new；MainViewModel 10 参（7 委托）；MainWindow 内部自己 new Runner+VM——组合根分裂在两处 | MainWindow.xaml.cs:47-58 | 把 Runner 提升到 App 创建后传入（1 行）；**不建议上 DI 容器**（组合根仅 60 行） | 半天 | 中性 |
| i18n | 中文硬编码约 208 处（XAML 62 + C# 146 + Core 6）；csproj 已声明 SatelliteResourceLanguages 但零 resx | SettingsWindow.xaml 43 处最密 | Strings.resx + LocalizeExtension，先抽 XAML | ~2 天 | **线性变贵**；确定只做中文可无限期推迟 |
| 对外集成 | CLI 3 开关逐条 if；命名事件无 payload（第三方只能唤出，不能"唤出并搜索 X"）；无 named-pipe/URL protocol | App.xaml.cs:14-15,48-61 | 事件后接命令协议或 NamedPipeServerStream（复用 ShowSignalListener 线程模式 ~100 行）+ CLI 集中分发 | 1 天 | 集成面目前仅 1 消费者，顺路 |
| 主题 | 画刷覆盖机制**本身就是**自定义主题的正确扩展点，但 accent 从未覆盖、无从文件加载 | ThemeService.cs:23-56,132-153 | settings.json 读 (key,color) 覆盖表 + ApplicationAccentColorManager | 1 天 | 随时可做，不随时间变贵 |
| 更新分发 | 零更新代码；版本硬编码 1.0.0；无 winget/Release | csproj | 单文件+单实例使自更新异常简单（后台 GET→temp→退出替换） | 1-2 天 | 仅当准备公开发布才有收益 |
| 测试 seam | 既有委托注入/纯静态函数模式已成型可推广；ClipboardMonitor（HwndSource+静态路径）、HotkeyService、StartupService（直连注册表）不可测 | — | 推广现有 seam，每服务 1-2 小时 | 低 | 渐进 |
| 便携化 | 数据目录写死 LOCALAPPDATA（static readonly，8 调用点）；互斥量/事件名硬编码不随目录派生 | AppPaths.cs:15-17、App.xaml.cs:14-15 | 改 `=>` + Initialize()，便携模式=exe 旁 marker；半天 | 半天 | 便携双副本需互斥名派生 |

**明确不建议**：DI 容器、FTS5（等 10k+ 条历史）、自更新（除非公开发布）。

---

## 3. 建议的实施批次（供裁定）

| 批次 | 内容 | 合计成本 | 说明 |
| --- | --- | --- | --- |
| **批一：快赢包**（推荐先做） | P-0 上限 bug + 粘贴为纯文本 + 托盘暂停捕获 + 粘贴后保持面板开关 + 图片另存为 | 全部 S 级，一次发布 | 五项都是小改动、高频收益，零架构改动 |
| **批二：体验中项** | 删除撤销 Toast + 记住面板尺寸/跨会话位置 + 搜索高亮 + 日期/来源过滤 | 2-3 天 | 交互语义需小幅设计（删除保留期、双击语义不受影响） |
| **批三：扩展性地基**（现在便宜以后贵） | SQLite user_version 迁移 + settings version 字段 + AppPaths 可重定向 + IActionExecutor 注册表 + 脚本 stdin JSON | 2-3 天 | 不改任何用户可见行为，纯地基；**拖得越久越贵** |
| **批四：可选大项**（按需逐个裁定） | 预览窗、多选批量、快速粘贴热键、历史导出导入、自定义主题色、i18n、NamedPipe 协议 | 每项 1-2 天 | 每项独立，随用随挑 |

---

## 4. 决策所需说明

- 批一/批三改动都不碰数据库 schema 与用户数据（批三的迁移基建只是**建立**演化机制，本轮不加列）。
- 删除撤销的保留窗口（如 5 秒 Toast 撤销）与隐私取舍需你确认口味。
- 快速粘贴热键 Ctrl+Alt+1..9 与现有 Win+Alt+V 唤出键并存，冲突回退已有现成模式。

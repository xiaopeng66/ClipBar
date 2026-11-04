# ClipBar 路线图（v1.1 → v1.3，可执行连续计划）

> 依据 `docs/IMPROVEMENT-PROPOSALS.md` 的两路审计（功能对照 + 架构可扩展性）编排。
> 工作约定：每个条目按「实现 → 自动化测试 → 发布 → 实测验收」闭环推进，条目间连续执行不停顿；
> 每个里程碑发布一个 GitHub Release；任何条目验收失败即停下修复，不带病前进。
>
> 基线：v1.0.0（388 例测试全绿，Release 资产 ClipBar.exe 70.4 MB / ClipBar-lite.exe 9.2 MB）。

---

## v1.1 — 快赢包（高频收益，零架构改动）

| # | 条目 | 范围 | 验收标准（全部满足才算完成） |
| --- | --- | --- | --- |
| 1.1 | **粘贴为纯文本** | 右键菜单加「粘贴为纯文本」：仅写回 UnicodeText（不带 HTML）后走既有粘贴链；文本类条目默认菜单项 | 右键文本条目 → 菜单含该项 → 执行后目标窗口收到无格式文本；带格式条目（HTML 来源）验证格式被剥离；单测覆盖 UnicodeText-only 写回分支 |
| 1.2 | **托盘「暂停捕获」** | TrayService 菜单加「暂停捕获/恢复捕获」互斥项；暂停时 ClipboardMonitor 早退；暂停状态托盘 tooltip 追加「（已暂停）」 | 暂停后复制内容不入库（DB 计数不变）；恢复后正常捕获；重启后恢复为开启 |
| 1.3 | **粘贴后保持面板开关** | 设置页「行为」加复选框「粘贴后保持面板打开」；PasteItem 按设置跳过 Hide() | 关（默认）→ 现行为不变；开 → 连续粘贴两次面板保持可见且焦点正确回目标；设置入 Normalize/ContentEquals |
| 1.4 | **图片另存为文件** | 右键图片条目加「图片另存为…」：SaveFileDialog 默认 `图片 %w×%h.png`；写盘失败弹 Toast | 保存成功且文件可打开；取消无副作用；三档预览下均可触发 |

**发布 v1.1**：新增测试（纯文本写回、暂停捕获、保持面板、另存为）全部通过；388+ 例全绿；Release 资产双 exe。

---

## v1.2 — 体验中项（误操作保护 + 日常手感）

| # | 条目 | 范围 | 验收标准 |
| --- | --- | --- | --- |
| 2.1 | **删除撤销** | 删除后 Toast「已删除 · 撤销」保留 5 秒；撤销 = 重新入库同 Id 条目（图片文件删除延迟到保留期结束：先移入 `images/trash/`，撤销移回，超时清扫） | 删除→撤销→条目原位恢复（含图片条目）；不撤销→5 秒后文件按现有路径清理；重启期间 trash 不残留 |
| 2.2 | **记住面板尺寸与位置** | ResizeMode=CanResize（Min 320×480）；关闭/隐藏时把 Left/Top/Width/Height 存入 AppSettings（Normalize/Clone/ContentEquals 同步）；启动按存储值恢复，无值时回落鼠标屏居中 | 调整尺寸→重启保持；拖动→重启位置一致；多显示器下恢复位置夹取在工作区内 |
| 2.3 | **搜索命中高亮** | 条目标题中对命中子串加强调色高亮（VM 层，不动 SQL） | 搜索「clip」时命中片段高亮、其余正常；中文/大小写不敏感 |
| 2.4 | **日期/来源过滤** | 筛选行追加「今天 / 7 天」与来源应用下拉（来自现有 SourceApp 去重） | 组合搜索/类型/时间过滤结果正确；清空筛选一键复位 |

**发布 v1.2**：同上验收 + 全量回归。

---

## v1.3 — 扩展性地基（现在便宜以后贵；不改任何用户可见行为）

| # | 条目 | 范围 | 验收标准 |
| --- | --- | --- | --- |
| 3.1 | **SQLite schema 迁移机制** | `InitializeAsync` 读 `PRAGMA user_version`，按版本递增执行迁移步骤（本轮 v1→v2 不加列，仅建立机制） | 新库 user_version=2；手工降级到 v1 的旧库自动升 2；`ReadItem` 不再怕缺列 |
| 3.2 | **settings.json 版本字段** | AppSettings 加 `Version`；JsonSettingsStore 写入带版本；Normalize 校验 Theme 合法值 | 旧文件（无 version）读入默认 1 并正常；未知 Theme 归一 |
| 3.3 | **数据目录可重定向** | AppPaths 改静态 Initialize（marker 文件 / `--data-dir` 参数决定目录）；互斥体与事件名随目录派生 | `--data-dir X` 启动后数据落在 X；无参数行为与现在完全一致 |
| 3.4 | **IActionExecutor 注册表** | 执行器 switch 改为 `Dictionary<SnipDoActionKind, IActionExecutor>`，组合根装配；runner 通知改注入 `Action<string>` | 现有 4 类动作行为回归不变（388+ 例）；新增动作类型只需注册 |
| 3.5 | **脚本 stdin JSON API** | PowerShell/Python 启动时经 stdin 传 `{"text":…,"html":…,"filePaths":[…],"sourceApp":…,"options":…}`；argv 保持兼容 | 旧脚本（只用 argv）行为不变；新脚本可读 stdin JSON 含 FilePaths；单测覆盖 JSON 序列化 |

**发布 v1.3**：行为零变化的回归证明（全部既有测试 + 新迁移/API 测试），Release 标注「内部地基版本」。

---

## Backlog（按需逐项裁定，不排期）

预览窗（全文/大图）· 多选批量删除 · 快速粘贴热键 Ctrl+Alt+1..9 · 历史导出/导入 · 自定义主题色（accent + JSON 覆盖表）· NamedPipe 命令协议（唤出并搜索）· i18n ResX 抽取 · 自动更新（需 GitHub Release 基础 ✓，待公开发布意愿明确）

---

## 执行纪律

1. 每个条目独立提交（feat/fix + 测试），里程碑收口发 Release；
2. 每条目完成即回填本表验收列为 ✅ 并附实测数据；
3. 主分支 main 保持可发布态；发版打 `vX.Y.Z` tag；
4. 不碰红线：真实数据目录、schema 兼容、SnipDo 目录名排序规则。

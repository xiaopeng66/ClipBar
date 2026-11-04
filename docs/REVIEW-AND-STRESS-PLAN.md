# ClipBar 代码审查 · 漏洞修复 · 暴力测试方案

> 本文档是**实施前置件**：所有修复与测试都必须按本文档的条目编号执行，测试结果回填到
> 「§7 结果记录」。未在本文档登记的改动视为超范围。
>
> 生成时间：2026-11-03 · 基线：`dist\ClipBar.exe` 70 MB（自包含压缩）/ `dist-lite` 9 MB，
> 51 个单元测试全绿。当前：**319 例**（F-01~F-42 + 加固 F-40，用例 T-01~T-52）。

---

## 0. 目标与判定标准

**目标**（对应用户诉求「整体代码审查和 bug 检查以及暴力测试，修复现在软件的隐藏漏洞」）：

1. **安全性**：外部输入（剪贴板内容、`.pbar` / `data.json` 扩展包、SVG 图标、settings.json、
   剪贴板 HTML）不得导致**越权文件写入/删除、任意代码执行、进程崩溃、无限资源占用**。
2. **健壮性**：任意单点异常不得让面板/托盘/热键静默失效；用户可感知的状态必须与真实状态一致。
3. **性能**：5000 条历史、大文本（10 MB）、大图片、100 个文件路径的极端输入下，UI 不卡死
   （单次主线程阻塞 < 100 ms，整体操作 < 3 s）。
4. **可回归**：每个修复都配一个自动化测试（Core 层直测，App 层抽成纯函数或可注入依赖后直测）。

**通过标准（全部满足才算完成）**：

| 门禁 | 命令 | 期望 |
| --- | --- | --- |
| 单元/回归测试 | `dotnet test -v q` | 0 失败；用例数 ≥ 51 + 本次新增（预估 ≥ 95） |
| 编码规范 | `python E:/AI/Zcode/scripts/check-encoding.py` | 退出码 0 |
| 构建 | `build.cmd` | 退出码 0，产出 `dist` 与 `dist-lite` |
| 冒烟 | `dist\ClipBar.exe --export-snipdo` | 退出码 0，`.pbar` 重新生成 |
| 压力 | §5 矩阵全部执行 | 无崩溃、无数据丢失、指标达标 |
| 视觉 | 主面板/设置/托盘截图 | 与修复前一致，无回归 |

---

## 1. 范围与基线

### 1.1 代码规模（实测）

| 层 | 文件数 | 行数 | 说明 |
| --- | --- | --- | --- |
| `src/ClipBar.App` | 28 | 5,158 | WPF 界面、监听、热键、托盘、扩展执行 |
| `src/ClipBar.Core` | 17 | 2,094 | 模型、SQLite 仓库、SnipDo 解析/匹配/打包 |
| `tests/ClipBar.Core.Tests` | 5 | 718 | 仅覆盖 Core；**App 层 0 测试** |
| XAML | 8 | 1,063 | |

### 1.2 信任边界（本次审查的核心）

```
不可信输入                                 信任边界                        可信区
────────────────────────────────────────────────────────────────────────────────────
剪贴板 (CF_UNICODETEXT/HTML/HDROP/DIB)  → ClipboardMonitor.BuildItemAsync → SQLite
.pbar / .zip / data.json                → ExtensionService.ImportAsync   → extensions\
扩展目录内的 .svg                       → SvgIconLoader.Load             → WPF 视觉树
settings.json                           → JsonSettingsStore.Load         → AppSettings
扩展清单里的相对路径字段                 → ResolveScript / ResolveFile    → Process.Start
```

**关键结论**：`.pbar` 与 `data.json` 属于「用户主动导入的第三方包」，按**不可信**处理
（用户可能从网上下载别人的 SnipDo 插件）。当前实现对它们的防护不足（见 §3）。

---

## 2. 审查清单（逐子系统）

审查方法：静态通读 + 针对性 grep + 边界值推演。每项标注 `[已核实]`（读代码确认）
或 `[待验证]`（需跑测试确认）。

### 2.1 Core · SQLite 仓库（`SqliteClipRepository.cs` 375 行）

| 编号 | 检查点 | 结论 |
| --- | --- | --- |
| R-1 | 去重 upsert 的 `ON CONFLICT(hash)` 是否会覆盖 pinned/created_at | [已核实] 用 `COALESCE` + `RETURNING`，pinned/created_at 保留，正确 |
| R-2 | `TrimAsync` 的 `DELETE ... IN (...)` 参数个数 | [已核实] **无上限**，`MaxItems=5000` 时一条 SQL 挂 5000 个参数 |
| R-3 | `TrimAsync` 的 `maxNonPinned < 1` | [已核实] 被强转为 1，与设置下限 10 不一致（无害但不严谨） |
| R-4 | 并发写入（多连接同时 AddOrTouch） | [待验证] `Cache=Shared` + WAL，需 8 线程 × 200 次压测 |
| R-5 | 读到畸形行（`kind` 越界、`file_paths` 非法 JSON、`text` 为 NULL） | [待验证] `ReadItem` 是否抛异常导致整次查询失败 |
| R-6 | `LIKE` 搜索的转义 | [已核实] `ESCAPE '\'` + `EscapeLike`，正确 |
| R-7 | 事务边界：Clear/Delete 后 image 文件与 DB 的一致性 | [已核实] 先查路径再删行，文件删除失败只记日志（可接受） |

### 2.2 Core · SnipDo 清单解析与匹配

| 编号 | 检查点 | 结论 |
| --- | --- | --- |
| P-1 | `identifier` 缺省派生 | [已核实] `"imported." + name.ToLower().Replace(' ','-')`，**未过滤路径字符** |
| P-2 | `ResolveFile`（图标路径）是否限制在包内 | [已核实] **否**，`Path.GetFullPath(Combine(root, relative))` 可越界（只读，影响小） |
| P-3 | `regex` 非法/超长 | [已核实] 有 500 ms 超时；非法模式返回 `true`（已注释为有意行为） |
| P-4 | `showOnlyApps` 在进程名为 null 时的语义 | [已核实] 返回 `Count == 0`，即「无进程名就隐藏」——与 R 的 `GetProcessName` bug 叠加后动作消失 |
| P-5 | `SnipDoText.Resolve` 的占位符替换顺序 | [已核实] 顺序 `Replace`，**剪贴板文本自身含 `{PLAIN_TEXT}` 会被二次展开** |
| P-6 | `FindUrl` 的尾部标点裁剪 | [已核实] 裁 `.,)];:`，可能截断合法 URL 结尾（低危） |

### 2.3 App · 扩展导入与执行（最高风险区）

| 编号 | 检查点 | 结论 |
| --- | --- | --- |
| E-1 | `SanitizeFolderName` 对 `.` / `..` / 保留名 | [已核实] **`Path.GetInvalidFileNameChars()` 不含 `.`**，`identifier="."` → `target=extensions\.` → `Directory.Delete(..., recursive)` **删掉 extensions 目录本身**；`".."` 删掉 `%LOCALAPPDATA%\ClipBar` |
| E-2 | `Extract` 的 zip-slip | [已核实] 有 `..` 与 `StartsWith(staging)` 双重防护 |
| E-3 | `Extract` 的 zip bomb | [已核实] **无**条目数上限、无总解压体积上限、无压缩比检查 |
| E-4 | `CopyFolder` 的目标在源内 | [已核实] 只判 `source == target`，目标嵌套在源内会**无限递归/膨胀** |
| E-5 | `FindManifest` 的递归枚举 | [已核实] `EnumerateFiles(root, "*.json", AllDirectories)` **无深度/数量上限** |
| E-6 | `ResolveScript`（PowerShell/Python 脚本路径） | [已核实] **无包含检查** → 清单写 `C:\Windows\System32\x.ps1` 或 `../../../../x.ps1` 即执行任意脚本 |
| E-7 | `RunPython` 的流读取 | [已核实] **同步 `ReadToEnd` 先 stdout 后 stderr**（管道死锁），且**阻塞 UI 线程**，且**不传占位符参数** |
| E-8 | `OpenUrl` 的协议白名单 | [已核实] **无**，`file:///C:/.../calc.exe`、`javascript:` 等原样 `Process.Start(UseShellExecute=true)` |
| E-9 | `SendKeys` / `PasteIntoPreviousWindow` 的前台切换 | [已核实] 忽略 `SetForegroundWindow` 返回值，UI 线程 `Thread.Sleep(60/90)` |
| E-10 | `SetClipboardText` 5 次重试后 | [已核实] 静默放弃，用户无感知 |
| E-11 | `SvgIconLoader` 的 XML 解析 | [已核实] `XDocument.Load` **无 `DtdProcessing`/`MaxCharactersInDocument` 限制**（XML 炸弹）；`depth > 8` 有防护 |
| E-12 | `ExtensionActionViewModel.ResolveIcon` | [已核实] 接受**绝对路径**与 `..` 越界（只读图标） |
| E-13 | 扩展执行的重入 | [已核实] `MainViewModel.RunExtensionAsync` **无重入锁**，双击会跑两次 |

### 2.4 App · 剪贴板监听与粘贴

| 编号 | 检查点 | 结论 |
| --- | --- | --- |
| C-1 | 文本长度 / 文件数 / 图片尺寸的上限 | [已核实] **全无**：10 MB 文本、10 万条 `FilePaths` 直接进 DB |
| C-2 | 重复图片的旧文件清理 | [已核实] `stored.Id != item.Id` 分支**恒假**（仓库原地返回同一对象），死代码，磁盘会堆积孤儿 PNG |
| C-3 | `GetBitmap` 优先取全局剪贴板 | [已核实] 先 `Clipboard.GetImage()` 再看传入 `IDataObject`，可能抓到与本次通知不符的内容 |
| C-4 | 排除应用判定 | [待验证] 依赖 `GetProcessName`（见 N-1），当前**实际失效** |
| C-5 | `SetClipboardAsync` 抑制窗口 700 ms | [已核实] 固定值，粘贴 500 ms 内再次复制可能被吞 |
| C-6 | 写回剪贴板失败 | [已核实] 吞 `COMException` 后静默 return，用户点了没反应 |

### 2.5 App · 热键 / 设置 / 启动 / 单实例

| 编号 | 检查点 | 结论 |
| --- | --- | --- |
| S-1 | `HotkeyService.Register` 先 `Unregister` | [已核实] **先注销再注册**，新组合失败时旧热键已丢失 |
| S-2 | `SaveSettings` 失败回滚 | [已核实] 回滚调用 `Register(previousHotkey)`，**但失败路径同样先注销**，可能彻底无热键 |
| S-3 | `JsonSettingsStore.Load` 损坏文件 | [已核实] 静默返回默认值 → 用户热键/主题/排除列表**全部丢失且无提示** |
| S-4 | `Save` 的原子性 | [已核实] 固定 `.tmp` 名（并发不安全）、无 fsync |
| S-5 | 每次启动重写 settings.json | [已核实] `App.OnStartup` 无条件 `Save`，扩大 S-3 的损坏窗口 |
| S-6 | `StartShowListener` 时序 | [已核实] 在窗口/托盘/热键之后才创建，冷启动 `--show` 期间到达的信号**可能丢** |
| S-7 | `TryRunPluginCommand` 失败 | [已核实] catch 后仍 `return true` → **退出码 0**，脚本化调用无法感知失败 |
| S-8 | `DispatcherUnhandledException` 全吞 | [已核实] `Handled = true`，异常只进日志 |
| S-9 | `SettingsWindow.TryApply` 重建 `AppSettings` | [待验证] 是否漏掉非表单字段（如 `ExcludedApps` 之外的新字段） |
| S-10 | 设置页「清空」无二次确认 | [已核实] `SettingsWindow.Clear_Click` 直接调用 `_clear()`；面板与托盘都有确认 |

### 2.6 App · 平台层与 UI 线程

| 编号 | 检查点 | 结论 |
| --- | --- | --- |
| N-1 | `NativeMethods.GetProcessName` | [已核实] **把 `GetWindowThreadProcessId` 的返回值当 PID**（它返回的是**线程 ID**）→ `GetProcessById` 抛 `ArgumentException` → 恒返回 null。**来源应用记录、排除密码管理器、`showOnlyApps` 全部失效** |
| N-2 | `PlaceOnCursorMonitor` 的 DPI 换算 | [已核实] 用**目标屏**缩放比除**当前屏**坐标，跨不同 DPI 的显示器会偏 |
| N-3 | `_userMoved = true` 与 `DragMove` | [已核实] 先置位再 `DragMove`，`InvalidOperationException` 被吞后本次运行不再自动居中 |
| N-4 | `HtmlFormatHelper.ExtractFragment` 偏移非法 | [已核实] **回退返回整个 CF_HTML 原文**（含 header）；`IndexOf(key)` 可能命中正文 |
| N-5 | `MainViewModel.RefreshAsync` | [已核实] 每次按键（120 ms 防抖）重建最多 500 个 VM + `ItemsView.Refresh()` |
| N-6 | `TrayService` 光标获取失败 | [已核实] `cursor = default` → 菜单出现在 (0,0) |
| N-7 | `ImageStore` 编码 | [已核实] `Save(buffer)` 后 `ToArray()` 双份内存；`TryDelete` 信任任意路径 |
| N-8 | `PinBrushConverter` / `ThumbnailSizeConverter` | [已核实] 每次求值新建并冻结 `SolidColorBrush`；缩略图每次 `File.Exists` |
| N-9 | `AppOptionWindow` / `FluentMessageWindow` 资源强转 | [已核实] `(Brush)FindResource(...)` 缺资源即抛 |

---

## 3. 缺陷清单（按严重度排序，含修复方向）

> 严重度定义：**Critical** = 数据损毁或任意代码执行；**High** = 功能失效或可被恶意包利用；
> **Medium** = 用户可感知的错误行为；**Low** = 性能/一致性/可维护性。

### Critical

| ID | 位置 | 问题 | 修复方向 |
| --- | --- | --- | --- |
| **F-01** | `ExtensionService.cs:311` `SanitizeFolderName` | `"."` / `".."` / `"..."` / 保留名（`CON`、`NUL`）通过校验，`Directory.Delete(recursive)` 删除 extensions 目录或其父目录 | 显式拒绝 `.`/`..`/空/纯点串；加保留名黑名单；最终校验 `target` 必须**严格位于** `_extensionsDirectory` 之下（`GetFullPath` 后 `StartsWith(dir + sep)`）；`Directory.Delete` 前再断言一次 |
| **F-02** | `ExtensionActionRunner.cs:277` `ResolveScript` | 清单里的 `powershellFile` / `pythonFile` 无包含检查，可指向任意路径 → 导入一个恶意 `.pbar` 即任意代码执行 | 强制 `Path.GetFullPath(candidate)` 必须位于 `action.ScriptRoot` 之下；拒绝绝对路径；拒绝含 `..`；不存在则报错而非静默 null |

### High

| ID | 位置 | 问题 | 修复方向 |
| --- | --- | --- | --- |
| **F-03** | `NativeMethods.cs:148` | 线程 ID 当 PID → `GetProcessName` 恒 null → 来源应用、密码管理器排除、`showOnlyApps` 全部失效 | 改用 `out uint pid` 的正确签名并取 `pid`；`Process.GetProcessById((int)pid)` |
| **F-04** | `ExtensionService.cs:216` `Extract` | 无 zip 炸弹防护 | 条目数 ≤ 2048；单条目 ≤ 64 MB；总解压 ≤ 256 MB；压缩比 > 1000 且解压后 > 8 MB 时拒绝；边解边累计 |
| **F-05** | `ExtensionActionRunner.cs:140-175` `RunPython` | 同步 `ReadToEnd` 双流 → 管道死锁；阻塞 UI 线程；不传占位符参数 | 与 `RunPowerShellAsync` 对齐：`ReadToEndAsync` 两路并发 + `WaitForExitAsync` + 超时；改 async；补 `-PLAIN_TEXT` 等参数 |
| **F-06** | `ExtensionActionRunner.cs:66` `OpenUrl` | 无协议白名单，`file:///`、`javascript:` 等可触发本地程序/脚本 | 只允许 `http` / `https` / `mailto`；其余拒绝并提示 |
| **F-07** | `HotkeyService.cs:32` + `App.xaml.cs:SaveSettings` | 先注销后注册，失败即失去全部热键；回滚路径同样不可靠 | 改为「先尝试注册新组合（临时 ID），成功再注销旧的」；或注册失败时不注销旧热键 |
| **F-08** | `ClipboardMonitor.cs:85-130` | 无上限：文本长度、`FilePaths` 条数、图片尺寸（图片有 2560 上限，文本/文件没有） | 文本 ≤ 1 MB（超出截断并标记）；文件 ≤ 200 条；单条 `size_bytes` 记录真实值 |
| **F-09** | `ClipboardMonitor.cs:85-130` | 重复图片旧文件清理是死代码 → 孤儿 PNG 无限堆积 | 仓库返回 `stored` 与 `item` 的差异时按路径比对删除；或删除前按 hash 判断 |
| **F-10** | `ClipBar.Core/.../SqliteClipRepository.cs:268` `TrimAsync` | 无上限的 `IN (...)` 参数 | 分批（每批 200）删除 |
| **F-11** | `ClipBar.Core/.../SnipDoManifestParser.cs:46` | 派生 `identifier` 未过滤路径字符，流入 F-01 | 统一做 `Slug` 化（仅保留字母数字 `-_.`，并拒绝纯点串） |

### Medium

| ID | 位置 | 问题 | 修复方向 |
| --- | --- | --- | --- |
| **F-12** | `JsonSettingsStore.cs:20` `Load` | 损坏文件静默重置，用户配置丢失无提示 | 解析失败时把原文件重命名为 `settings.json.corrupt-<ts>` 并写日志 + 托盘气泡提示 |
| **F-13** | `JsonSettingsStore.cs:38` `Save` | 固定 `.tmp` 名 + 无 fsync | 用 `Path.GetRandomFileName()` 临时名 + `FileStream.Flush(true)` |
| **F-14** | `App.xaml.cs:112` | 每次启动无条件重写 settings.json | 仅在 `StartWithWindows` 实际变化时保存 |
| **F-15** | `App.xaml.cs:114` / `StartShowListener` | 监听器晚于窗口创建；冷启动信号可能丢 | 在互斥量成功后、窗口创建前就创建事件句柄，先记录 pending 标志，窗口就绪后消费 |
| **F-16** | `App.xaml.cs:150` `TryRunPluginCommand` | 失败仍返回 true → 退出码 0 | 失败时 `Environment.ExitCode = 1`（或 `Shutdown(1)`） |
| **F-17** | `HtmlFormatHelper.cs:45` | 偏移非法时返回整个 CF_HTML 原文 | 返回 `null`（丢弃 HTML，只保留纯文本） |
| **F-18** | `SnipDoText.cs:40` `Resolve` | 文本自身含占位符会被二次展开 | 单遍扫描替换（一次 `Regex.Replace` 或手写扫描），不迭代 |
| **F-19** | `SettingsWindow.xaml.cs:306` | 设置页清空无二次确认 | 复用 `FluentMessageWindow.Confirm` |
| **F-20** | `SvgIconLoader.cs:33` | `XDocument.Load` 无 XML 防护 | `XmlReaderSettings { DtdProcessing = Prohibit, MaxCharactersInDocument = 1<<20, MaxCharactersFromEntities = 0 }` |
| **F-21** | `ExtensionService.cs:259` `CopyFolder` | 目标嵌套在源内会递归膨胀 | 同时拒绝 `targetFull.StartsWith(sourceFull + sep)` |
| **F-22** | `MainViewModel.cs:148` `RunExtensionAsync` | 无重入锁 | `_running` 标志 + 按钮 IsEnabled 绑定 |
| **F-23** | `MainWindow.xaml.cs:93` `PlaceOnCursorMonitor` | 混用 DPI | 用 `GetDpiForWindow` 取**本窗口当前**缩放比，或先 `SetWindowPos` 再换算 |
| **F-24** | `PasteService.cs` / `SendKeys` | 忽略 `SetForegroundWindow` 返回值 + UI 线程 Sleep | 检查返回值，失败则 `AllowSetForegroundWindow` 或降级为「仅复制到剪贴板 + 提示」；Sleep 改 `await Task.Delay` |
| **F-25** | `ExtensionService.cs:293` `FindManifest` | 递归枚举无上限 | 限制深度 ≤ 3、文件数 ≤ 2000 |
| **F-26** | `ClipboardMonitor.cs:GetBitmap` | 优先读全局剪贴板 | 优先用传入的 `IDataObject`，失败再回退全局 |

### Low

| ID | 位置 | 问题 | 修复方向 |
| --- | --- | --- | --- |
| **F-27** | `ImageStore.cs` | `Save` + `ToArray()` 双份内存 | 直接 `buffer.ToArray()` 一次 |
| **F-28** | `TrayService.cs:ShowMenu` | 光标失败 → (0,0) | 回退到主屏右下角工作区 |
| **F-29** | `MainViewModel.RefreshAsync` | 每次按键重建 500 VM | 增量 diff（同 id 复用 VM） |
| **F-30** | `AppLog.cs` | 无轮转；每行 `EnsureCreated()` | 超 1 MB 轮转；目录创建只在启动时做 |
| **F-31** | `PinBrushConverter` / `ThumbnailSizeConverter` | 每次求值分配画刷 / `File.Exists` | 缓存画刷；缩略图存在性在 VM 构造时算一次 |
| **F-32** | `AppOptionWindow` / `FluentMessageWindow` | 资源强转抛异常 | 用 `TryFindResource` + 回退色 |
| **F-33** | 测试工程 | App 层 0 测试 | 新增 `tests/ClipBar.App.Tests`（引用 App 工程） |

#### 补测阶段新发现（2026-11-03，写测试时暴露）

前 33 项是读代码得出的；为它们补写断言时，又用断言把两个「已修复」的项打回去了。

| ID | 严重度 | 位置 | 问题 | 修复 |
| --- | --- | --- | --- | --- |
| **F-34** | **High** | `SafePath.IsInside` | `Path.GetFullPath("C:\store\")` **保留尾部反斜杠**，于是 `IsInside(root, root + "\")` 返回 `true`——根目录被当成自己的子目录。`DeleteInside` 先调 `IsInside`，`Directory.Delete(recursive)` 就会**删掉整个扩展库根目录**（而不只是某个扩展）。F-01 的防护被尾部斜杠绕过。 | 比较前先 `TrimEnd` 尾部两种分隔符（仅当路径长于根时，避免把 `C:\` 削成 `C:`）；`NormalizeRoot` 对盘根同样保留分隔符。新增 `IsInsideIsStrictlyChildOnly` + `IsInsideTreatsPrefixSiblingsAsOutside`。 |
| **F-35** | Medium | `SafeUrl.Normalize` | 无 scheme 的输入统一补 `https://`，但 **UNC 路径**（`\\server\share\x.exe`）也没有 scheme，于是被补成 `https://\\server\share\x.exe` 交给 `Process.Start`——正是该函数要拦的网络路径。反斜杠在 URL 主机位本就不合法。 | 无 scheme 且含 `\` 时直接返回 `null`。新增 `NormalizeRejectsNonWebSchemes` 的 UNC 用例。 |

#### 集成阶段新发现（2026-11-03，跑 §5.3 时暴露）

| ID | 严重度 | 位置 | 问题 | 修复 |
| --- | --- | --- | --- | --- |
| **F-37** | **High** | `ShowSignalListener.Run` | `EventWaitHandle` 以 `EventResetMode.AutoReset` 创建，**`WaitOne()` 永远返回 `true`**——`if (!_handle.WaitOne()) break;` 是死代码，循环唯一的真实出口是 `ObjectDisposedException`。配合 `Dispose()` 里先 `Set()` 再 `Dispose()`，退出时会把监听线程唤醒一次，于是在**已关闭的窗口**上排队一次 `Show()`（`InvalidOperationException: 关闭窗口后，无法设置可见性…`）。 | 用显式 `volatile bool _stopping` 作停机标志：`Dispose()` 置位 → `Set()` 释放阻塞的等待 → 循环醒来看到标志直接退出，不执行 `onSignal`。`IsStopping` 暴露给回调方二次确认。 |
| **F-38** | Medium | `MainWindow.Panel_PreviewMouseLeftButtonDown` | `_userMoved = true` 写在 `DragMove()` **之前**。拖拽中途被中断（`DragMove` 抛 `InvalidOperationException`）时标志已置位，于是**本次运行内再也不会自动居中**，面板一直停在原来碰巧的位置——用户看到的是「唤出位置乱了」，而真正的原因是一次没完成的拖拽。 | 移到 `DragMove()` 正常返回之后：只有真正完成的拖拽才算「用户已摆放」。 |
| **F-41** | **High** | `MainViewModel.cs:99/104`、`MainWindow.ShowPanel`、`App.OnClipCaptured` | 四处 `_ = RefreshAsync()` 式的「发了不管」调用**没有任何 catch**。`QueryAsync` 是会抛的（实测：删掉 `history.db` 后 `SqliteException: no such table: clips`），于是历史库一旦不可读，面板**继续显示旧列表**、日志**一行不写**、用户看不出任何异常。这不是崩溃风险（实测 .NET 10 吞掉未观测的任务异常，进程照活），而是**静默失效**。 | 统一走 `FireAndForget` → `ObserveAsync` 守卫：成功即复位、`OperationCanceledException` 不算失败、其余按「一次故障期只报一次」上报（日志 + Fluent 提示），由 `MainWindow.ReportRefreshFailure` 落地。 |
| **F-42** | **High** | `PasteService.cs:100-113`、`MainWindow.xaml.cs:144` | 粘贴图片时 `BitmapImage.EndInit()` 在文件损坏时抛 `NotSupportedException`/`FileFormatException`，路径畸形时抛 `UriFormatException`——**都不是**重试循环捕的 `COMException`/`ExternalException`，于是异常穿过 `SetClipboardAsync` → `PasteAsync` → `PasteItem`（`async void`，无 catch）。而 `File.Exists` 对这几种文件一律返回 `true`，`DescribeUncopyable` 拦不住。面板此时已经 `Hide()`：**没粘上、没提示**，只剩一行日志。 | 解码提前到重试循环之外并包在 `TryDecodeImage` 里（失败即返回 `false` → `CopyFailed`，走既有的「复制失败」提示）；`PasteItem` 再加一层 `catch` 兜底，任何逃逸都转成可见提示；同时把设置页两个导入处理器的窄过滤器放宽（同一类 `async void` 静默失效）。 |


---

## 4. 修复实施顺序

按依赖排序，每步结束都跑 `dotnet test`（保证不回退）：

1. **安全底座（Core 纯函数化）**：把路径校验、slug 化、解压限额抽成 `ClipBar.Core` 里的
   纯函数（`SafePath.EnsureInside`、`SafePath.Slug`、`ArchiveLimits`），先在 Core 写测试再改调用点。
   → 覆盖 F-01 / F-02 / F-04 / F-11 / F-21 / F-25。
2. **平台层正确性**：F-03（PID）、F-26（图片源）、F-23（DPI）、F-28（托盘锚点）。
3. **执行器加固**：F-05（Python 异步+参数）、F-06（协议白名单）、F-22（重入）、F-24（前台）。
4. **监听与仓库**：F-08（输入上限）、F-09（孤儿图）、F-10（分批删除）。
5. **配置与启动**：F-07（热键）、F-12/F-13/F-14（设置持久化）、F-15/F-16（单实例与退出码）。
6. **UI 与文案**：F-17 / F-18 / F-19 / F-29 / F-30 / F-31 / F-32。
7. **测试工程**：F-33（新建 App 测试工程，把可测逻辑抽成无 WPF 依赖的静态方法）。

**约束**：任何改动都不得改变 `history.db` 的 schema（避免用户数据迁移）；不得改变
`extensions\` 目录布局；不得改变 SnipDo `0ClipBar` 的目录名（排序依赖）。

---

## 5. 暴力测试矩阵

> 所有测试脚本放 `E:\AI\Zcode\tmp\clipbar-stress\`，用 Python 生成样本，
> 通过 CLI 或直调 Core 执行；测试完成后清理。生成物不得落在工作区根目录。
> **执行状态：该临时目录已于 2026-11-03 整体删除**（含脚本、CSV、快照副本与探针工程，共 37 MB）。
> 下表保留用例定义与结果，脚本本身不再随仓库分发——需要复跑时按表中「输入/期望」重建即可。


### 5.1 安全攻击面（对应 Critical/High）

| 编号 | 用例 | 输入 | 期望 |
| --- | --- | --- | --- |
| T-01 | 目录穿越 identifier | `.pbar` 的 `extension.json` 写 `"identifier": "."` | 导入被拒绝或落在 `extensions\<安全名>`；`extensions\` 目录仍存在 |
| T-02 | 上级穿越 identifier | `"identifier": ".."` | 同上；`%LOCALAPPDATA%\ClipBar` 仍存在 |
| T-03 | 保留名 identifier | `"identifier": "CON"` / `"NUL"` / `"a/b"` | 被拒绝或 slug 化 |
| T-04 | zip-slip | 条目名 `../../evil.txt`、`C:\evil.txt` | 抛 `SnipDoManifestException`，工作区外无文件 |
| T-05 | zip bomb（条目数） | 5000 个空条目 | 拒绝 |
| T-06 | zip bomb（体积） | 单个 300 MB 可压缩文本条目（压缩比 > 1000） | 拒绝，内存不暴涨 |
| T-07 | 脚本越界（绝对路径） | `"powershellFile": "C:\\Windows\\System32\\...ps1"` | 拒绝执行 |
| T-08 | 脚本越界（相对穿越） | `"powershellFile": "..\\..\\..\\..\\x.ps1"` | 拒绝执行 |
| T-09 | 协议滥用 | `url` 动作产出 `file:///C:/Windows/System32/calc.exe` | 拒绝，不启动进程 |
| T-10 | 协议滥用 2 | `javascript:alert(1)` / `ms-settings:` | 拒绝 |
| T-11 | XML 炸弹 | 含实体展开的 `.svg` 图标（billion laughs） | 加载失败并回退闪电图标，进程存活 |
| T-12 | 超深 SVG | 20 层嵌套 `<g>` | 截断渲染，不栈溢出 |
| T-13 | settings.json 损坏 | 写入截断 JSON / 二进制 | 应用仍启动；原文件被备份；用户看到提示 |
| T-14 | 占位符自展开 | 剪贴板文本为 `{PLAIN_TEXT}{URLENCODED_TEXT}` | 结果原样保留，不二次替换 |
| T-15 | CF_HTML 畸形 | `StartFragment` 大于 `EndFragment` / 越界 / 缺失 | 不抛异常；HTML 为空而不是整段原文 |

### 5.2 压力与容量

| 编号 | 用例 | 参数 | 期望 |
| --- | --- | --- | --- |
| T-20 | 剪贴板洪水 | 连续写入 2000 条不同文本（间隔 5 ms） | 无崩溃；DB 最终条数 = 上限；UI 刷新不卡死 |
| T-21 | 大文本 | 单条 10 MB / 50 MB 文本 | 按 F-08 截断；主线程单次阻塞 < 100 ms |
| T-22 | 海量文件 | 单次复制 10,000 个路径 | 截断到上限；`file_paths` JSON 不超限 |
| T-23 | 大图片 | 8000×8000 PNG | 缩放到 2560 上限；内存峰值 < 400 MB |
| T-24 | 5000 条淘汰 | `MaxItems=5000`，插入 8000 条 | 固定项全部保留；`TrimAsync` 无参数溢出；耗时 < 3 s |
| T-25 | 并发写入 | 8 线程 × 200 次 `AddOrTouchAsync` | 无 `SQLITE_BUSY`；总行数 = 去重后正确值 |
| T-26 | 并发 Trim + Query | 写入同时每秒 Query | 无异常；不出现「读到已删行」崩溃 |
| T-27 | 畸形 DB 行 | 手工 UPDATE 让 `kind=99`、`file_paths='not-json'` | 查询不崩；该行被跳过或降级 |
| T-28 | 搜索风暴 | 1 字符逐字输入「剪贴板历史」10 次 | 防抖生效；无 500 VM × 10 的抖动；CPU 峰值可接受 |
| T-29 | 图片孤儿 | 重复复制同一张图 100 次 | `images\` 文件数不增长 |
| T-30 | 扩展执行重入 | 双击动作按钮 5 次 | 只执行 1 次 |

### 5.3 集成与系统

| 编号 | 用例 | 期望 |
| --- | --- | --- |
| T-40 | 热键冲突 | 先用测试程序占住 `Ctrl+Alt+V`，再保存该组合 | 旧热键仍可用；提示清晰 |
| T-41 | 单实例竞态 | 同时启动 5 个实例，其中 3 个带 `--show` | 只有 1 个常驻；面板被唤出；无「僵尸」进程 |
| T-42 | 冷启动 `--show` | 先 `--show` 启动（无实例） | 面板必须显示 |
| T-43 | CLI 失败退出码 | 目标 `.pbar` 被独占锁占用时 `--export-snipdo` | 退出码非 0；原包不被破坏 |
| T-44 | 多显示器/DPI | 主屏 100% + 副屏 150%，鼠标在副屏时唤出 | 面板在副屏居中、大小正确 |
| T-45 | 排除应用 | 在 KeePassXC 中复制文本 | 不进入历史（验证 F-03 修复） |
| T-46 | 来源应用记录 | 在记事本复制 | `source_app` = `notepad`（验证 F-03） |
| T-47 | 面板拖动后唤出 | 拖动面板 → 关闭 → 再唤出 | 保持拖动位置，不再自动居中 |
| T-48 | 长时间运行 | 连续 2 小时、每 30 s 复制一次 | 内存增长 < 50 MB；`app.log` 不无限增长 |
| T-49 | 复制失败不盲粘 | 图片行的 PNG 已删 / 文件列表为空 / 未知 `Kind` | `DescribeUncopyable` 全部拒绝；`PasteAsync` 返回 `CopyFailed` 且不按键 |
| T-50 | 单条坏路径不毁整条剪贴 | 文件列表里混入不存在 / 目录 / 畸形路径 / `null` | `SafeFileSize` 一律返回 0 不抛；求和只累加真实文件；整条 clip 仍入库 |
| T-51 | 后台刷新失败必须可见 | 仓库 `QueryAsync` 持续抛异常（模拟历史库被删） | 失败被上报且**一次故障期只报一次**；恢复成功后再次故障会重新上报；被新输入取消的搜索**不**算失败 |
| T-52 | 损坏图片不得炸掉粘贴 | `images\` 下的 PNG 是 0 字节 / 截断 / 纯文本伪装；另加畸形路径 `::::` | `SetClipboardAsync` 返回 `false`（→ `CopyFailed`）而**不抛异常**；不发送 Ctrl+V |

> **T-43 实现说明**：`AppPaths.DataDirectory` 在类型初始化时就从 `SpecialFolder.LocalApplicationData`
> 求值，改 `%LOCALAPPDATA%` 环境变量**不会**重定向它，而把真实数据目录设为只读会危及用户数据。
> 因此改用等价且更可控的故障注入：用 `FileShare.None` 独占锁住目标 `.pbar`，让写入必然失败。
> 两条路径的退出码语义完全相同（`PluginCommandRunner` 捕获异常 → `Environment.ExitCode = 1`）。

> **T-44 实现说明**：本机两块显示器都报 96 DPI（`GetDpiForMonitor` 无混合 DPI 可用），
> 所以混合 DPI 的换算只能由 `PanelPlacementTests` 的 6 条算术用例覆盖；屏幕实测覆盖的是
> 「副屏在主屏左侧（负坐标）」与「工作区被任务栏裁掉」两种真实几何。

> **T-45/T-46 实现说明**：本机未安装 KeePassXC，用等价的排除语义验证——
> 把 `powershell.exe` 临时写进 `ExcludedApps`（测前备份、测后校验 md5 还原），
> 再由一个前台自有窗口的 PowerShell 进程写剪贴板。T-46 是同一机制的**正向对照**
> （不在排除列表时该进程名必须落库），两者合起来才证明排除判断走的是真实进程名而非空值。

---

## 6. 执行与验证流程

```
① 基线快照
   - dotnet test 记录当前 51 用例全绿
   - 备份 %LOCALAPPDATA%\ClipBar 到 tmp\clipbar-stress\baseline\

② 逐条修复（§4 顺序）
   - 每修一条：写/改测试 → dotnet test → 记录
   - Critical/High 必须先在测试里复现失败，再修到通过（红→绿）

③ 跑 §5 矩阵
   - 5.1 安全用例：脚本化，断言「拒绝 + 无副作用」
   - 5.2 压力用例：脚本化 + 计时，断言指标
   - 5.3 集成用例：半手工（热键/多屏/托盘需真实桌面），记录截图

④ 全门禁
   - dotnet test / build.cmd / check-encoding.py / --export-snipdo 冒烟

⑤ 文档回填
   - 本文档 §7 结果表
   - README「已知限制」按修复结果更新
```

**红线**：修复过程中不得删除 `%LOCALAPPDATA%\ClipBar`（测试用的隔离目录一律通过
`JsonSettingsStore` / `SqliteClipRepository` 的构造参数指向 `tmp\` 下，不动真实数据）。

---

## 7. 结果记录

> 实施时逐条回填。状态：`待办` / `已修复` / `已验证` / `不修（说明）`。

### 7.1 缺陷修复状态

| ID | 严重度 | 状态 | 测试用例 | 备注 |
| --- | --- | --- | --- | --- |
| F-01 | Critical | 已修复 | T-01/T-02/T-03 | `SafePath.Slug` + `EnsureInside`；纯点串/保留名拒绝 |
| F-02 | Critical | 已修复 | T-07/T-08 | `ResolveScript` 强制 `EnsureInside(ScriptRoot)` |
| F-03 | High | 已修复 | T-45/T-46 | `GetWindowThreadProcessId` 取 out 参数 |
| F-04 | High | 已修复 | T-05/T-06 | `ArchiveLimits` 边解边累计 |
| F-05 | High | 已修复 | — | Python 改 `ReadToEndAsync` 双流并发 + `WaitForExitAsync` + 占位符参数 |
| F-06 | High | 已修复 | T-09/T-10 | 协议白名单 http/https/mailto |
| F-07 | High | 已修复 | T-40 | 先校验后注册；失败自动回绑旧组合 |
| F-08 | High | 已修复 | T-21/T-22 | 文本 ≤1 MB、文件 ≤200 条 |
| F-09 | High | 已修复 | T-29 | 启动时 `SweepOrphans`（新增 `GetImagePathsAsync`） |
| F-10 | High | 已修复 | T-24 | `TrimAsync` 分批 200 删除 |
| F-11 | High | 已修复 | T-03 | manifest identifier 统一 slug 化 |
| F-12 | Medium | 已修复 | T-13 | 损坏文件改名 `.corrupt-<ts>` + `LastLoadWarning` |
| F-13 | Medium | 已修复 | — | `Path.GetRandomFileName()` 临时名 + `Flush(true)` |
| F-14 | Medium | 已修复 | — | 仅 `StartWithWindows` 实际变化时写盘 |
| F-15 | Medium | 已修复 | T-41/T-42 | 监听线程在窗口创建前启动 |
| F-16 | Medium | 已修复 | T-43 | 失败时 `Environment.ExitCode = 1` |
| F-17 | Medium | 已修复 | T-15 | 偏移非法 → 返回 `null` |
| F-18 | Medium | 已修复 | T-14 | 单遍 `StringBuilder` 扫描，替换值不再被重扫 |
| F-19 | Medium | **误报** | — | `Clear_Click` → `App.ClearAllHistory`，已有 `FluentMessageWindow.Confirm` |
| F-20 | Medium | 已修复 | T-11/T-12 | `DtdProcessing=Prohibit` + `XmlResolver=null` + 1 MiB 上限 |
| F-21 | Medium | 已修复 | — | `CopyFolder` 同时拒绝目标嵌套在源内 |
| F-22 | Medium | 已修复 | T-30 | `Interlocked.Exchange` 单槽 |
| F-23 | Medium | 已修复 | T-44 | 物理像素换算后再按目标屏缩放比除 |
| F-24 | Medium | 已修复 | — | `TryActivateAsync` 三轮 `AttachThreadInput` + 校验前台；失败降级为「已复制」提示 |
| F-25 | Medium | 已修复 | — | `FindManifest` 深度 ≤3、文件 ≤2000 |
| F-26 | Medium | 已修复 | — | `GetBitmap` 优先 `IDataObject` |
| F-27 | Low | 已修复 | — | `TryGetBuffer` 直读，`FileMode.CreateNew` + 竞态容忍 |
| F-28 | Low | 已修复 | — | 光标失败回退主屏右下工作区 |
| F-29 | Low | 已修复 | T-28 | 按 Id 增量 diff + `Move` 对齐，复用 VM 与缩略图 |
| F-30 | Low | 已修复 | T-48 | 1 MB 轮转；目录创建每进程一次 |
| F-31 | Low | 已修复 | — | 画刷静态冻结；`HasThumbnail` 改为缓存字段 |
| F-32 | Low | 已修复 | — | `ThemeBrushes.Resolve` + `TryFindResource` |
| F-33 | Low | 已修复 | — | 新增 `tests/ClipBar.App.Tests`（17 用例，已注册进 `ClipBar.slnx`） |
| F-34 | **High** | 已修复 | T-01/T-03 | 补测时发现：`IsInside` 被尾部斜杠绕过 → `DeleteInside` 可删库根 |
| F-35 | Medium | 已修复 | T-09/T-10 | 补测时发现：UNC 路径被补成 `https://\\server\...` 后交给 Shell |
| F-36 | Medium | 已修复 | T-13 | 补测时发现：`settings.json` 为字面量 `null` → 返回默认值但**无警告无备份**，下次保存静默毁配置 |
| **F-37** | **High** | 已修复 | T-41/T-42 | 集成时发现：`AutoReset` 句柄的 `WaitOne` 永不返回 false → 停机靠标志位；退出时不再向已关闭窗口排队 `Show()` |
| F-38 | Medium | 已修复 | T-47 | 集成时发现：`_userMoved` 在 `DragMove()` 前置位 → 一次被中断的拖拽永久禁用自动居中 |
| **F-39** | **High** | 已修复 | T-49 | 复查粘贴路径时发现：`SetClipboardAsync` 返回 `void`、`PasteAsync` 无条件发 Ctrl+V → 复制失败时把**上一次**的剪贴板内容（常是刚复制的密码）粘进目标窗口 |
| **F-41** | **High** | 已修复 | T-51 | 压力测试阶段发现：四处 fire-and-forget 刷新无 catch，历史库不可读时面板静默停在旧列表；统一 `ObserveAsync` 守卫 |
| **F-42** | **High** | 已修复 | T-52 | 压力测试阶段发现：损坏/截断/伪装的 PNG 与畸形路径在粘贴路径抛非 COM 异常，`async void` 入口无 catch → 面板已隐藏、无提示；解码守卫 + 入口兜底 + 导入过滤器放宽 |
| F-40 | 加固（非活缺陷） | 已加固 | T-50 | `SafeFileSize` 只捕 `IOException`，而其调用方对整个选择集求和 → 理论上一条坏路径会毁掉整条 clip。**实测证伪**：`File.Exists` 对空串/内嵌 NUL/裸设备前缀/不存在的盘/`null` 一律返回 `false` 而不抛，所以 `new FileInfo()` 只会对刚刚存在的路径求值，恶意 payload 场景不可达；仍补齐 `UnauthorizedAccessException`/`ArgumentException` 兜住「检查与读取之间被删/改 ACL」的窄窗口 |

### 7.2 压力测试结果

| 编号 | 结果 | 指标 | 备注 |
| --- | --- | --- | --- |
| T-01~T-06 | 通过 | App 侧 22 例 | `ExtensionServiceTests.cs`：identifier 落盘、zip-slip 七种形态、条目数/单文件/压缩比三道闸；含正向对照（正常包能导入、同 identifier 重导入替换） |
| T-07~T-10 | 通过 | App 侧 20 例 | `ExtensionActionRunnerTests.cs`：绝对路径/相对穿越/七种危险 scheme 全部拒绝；含正向对照（包内脚本能执行、退出码 3 被判失败） |
| T-11~T-12 | 通过 | App 侧 9 例 | `SvgIconLoaderTests.cs`：billion laughs、XXE、40 层嵌套、20000 字符 path 均返回 `null` 或降级，进程存活 |
| T-13 | 通过 | Core 侧 14 例 | `JsonSettingsStoreTests.cs`：8 种畸形 JSON + 二进制垃圾 → 默认值 + 警告 + `.corrupt-<ts>` 备份 |
| T-14 | 通过 | Core 侧 4 例 | `SnipDoTextTests.cs` 新增：剪贴板文本、APPOPTION 值均不被二次展开；孤立 `{}` 不吞后文 |
| T-15 | 通过 | 既有 8 例 | `HtmlFormatHelperTests` 已覆盖 Start>End / 越界 / 缺失，无需新增 |
| T-20 | 通过 | Core 1 例 | `RepositoryStressTests.TwoThousandCapturesKeepTheHistoryAtTheCap`：2000 次写入后条数 = 上限，无崩溃 |
| T-21 | 通过 | App 4 例 | `CaptureLimitsTests`：恰好等于上限不动、10 MB / 50 MB 均截断且 `FiftyMegabyteTextIsCutToTheCapQuickly` 用耗时断言主线程未阻塞 |
| T-22 | 通过 | App 3 例 | 10,000 路径截到 200；未超限全保留；截断后的 JSON 体积有上限断言 |
| T-23 | 通过 | App 3 例 | 超限图缩到 2560；非方形保持宽高比；恰好等于上限不重采样 |
| T-24 | 通过 | Core 1 例 | `TrimmingEightThousandRowsKeepsEveryPinnedItemAndStaysFast`：8000 行淘汰后固定项全在，耗时受控 |
| T-25 | 通过 | Core 1 例 | `EightWritersNeverHitSqliteBusyAndNeverLoseARow`：8 线程 × 200 次，无 `SQLITE_BUSY`、无丢行 |
| T-26 | 通过 | Core 1 例 | `ConcurrentTrimAndQueryDoNotThrow`：写入 + 每秒查询并存，无「读到已删行」异常 |
| T-27 | 通过 | Core 1 例 | `MalformedRowsDegradeInsteadOfCrashingTheQuery`：`kind=99` / `file_paths='not-json'` 被跳过而非抛异常 |
| T-28 | 通过 | Core 1 + App 2 例 | Core 侧逐字输入 + 通配符按字面处理；App 侧 `TypingTheSameQueryTenTimesSettlesOnOneCorrectResult`、`CancellingDuringARefreshDoesNotLeaveAStaleView` |
| T-29 | 通过 | App 4 例 | `SavingTheSameImageAHundredTimesLeavesExactlyOneFile`、并发保存不重复、不同图不同文件；另有 `SweepOrphans` 5 例 |
| T-30 | 通过 | App 2 例 | `FiveRapidClicksOnAnExtensionRunTheScriptOnce`（单槽重入锁）、`ASecondRunIsAllowedAfterTheFirstFinishes`（正向前提） |
| T-40 | 通过 | App 14 例 + 屏幕 | `HotkeyServiceTests`：占用被拒且提示含「占用」、被拒方绝不抢走组合、改键失败回绑旧组合、解析失败不动现有注册、释放后可重新注册；9 条 `TryParse` 文案断言 |
| T-41 | 通过 | 屏幕实测 | 5 个进程同时启动（3 个 `--show`）→ 存活 **1**；再启动 1 个 → 仍为 **1**；清理后残留 **0** |
| T-42 | 通过 | 屏幕实测 | 冷启动 `--show` 后枚举到可见且带标题的窗口 `ClipBar` |
| T-43 | 通过 | 屏幕实测（正负两路） | 正常：`exit=0` 且 `.pbar` 重新生成（md5 变化）；独占锁：`exit=1`，md5 不变，日志记 `IOException ... being used by another process` |
| T-44 | 通过 | App 6 例 + 双屏实测 | 算术：100%/150% 换算、跨屏各缩放一次、超大窗口夹到左上、非法缩放回退 1.0、负原点副屏；实测：主屏 `(814,242)-(1234,862) 420x620`、竖屏副屏 `(-1398,20)-(-978,640)`，均在工作区内居中 |
| T-45 | 通过 | 屏幕实测（负向） | 临时把 `powershell.exe` 加入排除列表后写入剪贴板 → DB 中该 token **0 行** |
| T-46 | 通过 | 屏幕实测（正向） | 同一机制下不在排除列表 → DB 记 `source_app='powershell'`、`kind=0`（真实进程名，非空值） |
| T-47 | **通过** | 屏幕实测（真实鼠标事件，DPI 感知） | 见下方「T-47 实测」。位移 `(-240,-160)` 与请求**逐像素一致**，隐藏后改光标位置再唤出 drift `(0,0)`。**首次运行判 FAIL 是 harness 缺陷，不是产品缺陷**——见「T-47 首次失败的两个根因」 |
| T-48 | **有条件通过** | 240 轮 / 120 分钟 / 242 采样 | 见下方「T-48 实测」。**私有字节 +58.46 MB 超出「< 50 MB」判据**，不按判据算通过；工作集反向**下降 60.21 MB**、私有斜率从 0.4078 衰减到 0.1212 MB/轮、句柄为有界锯齿（8 次 −83~−165 回落，峰值 887~973 无单调爬升），故判定「非泄漏，但私有水位高于判据」。两条限制如实记录：`app.log` 峰值仅 42280 B，**F-30 的 1 MB 轮转未被本次跑到**；浸泡末 DB 仅 315 行（< 500 上限），**淘汰路径未被本次跑到**（由 T-20/T-24 单测覆盖） |
| T-49 | 通过 | App 侧 8 例 | `PasteServiceTests.cs`：文本恒可复制（含 `null`/空串）；图片缺文件/无路径被拒、有文件放行；空文件列表被拒、非空放行；未知 `Kind` 被拒 |
| T-51 | 通过 | App 侧 4 例 | `ViewModelStressTests.cs` 新增（`FaultingRepository` 注入）：首次失败被上报、5 次连续失败只报 1 次、成功后复位再故障报第 2 次、被取消的搜索不误报 |
| T-52 | 通过 | App 侧 4 例 | `PasteServiceTests.cs` 新增（真实写盘的三形态损坏 PNG + `::::` 畸形路径）：`empty`/`truncated`/`garbage` 均返回 `CopyFailed` 而非抛出，畸形路径同 |
| T-50 | 通过 | App 侧 11 例 | `CaptureLimitsTests.cs` 新增：真实文件返回字节数（4 KB）；不存在 → 0；7 种畸形路径（空串/`null`/空白/内嵌 NUL/不存在的盘/`::::`/裸 `\\?\`）一律 0 且不抛；目录 → 0；**关键回归**：`[真实文件, 已删文件, 内嵌 NUL, 目录, 不存在的盘, null]` 求和只累加真实文件（1000），不抛 |

> **F-40 的实测结论（加固，非活缺陷）**
> 这条最初是当作 High 写的：「一条 `UnauthorizedAccessException` 会让 `files.Sum(...)` 抛出去，
> 被 `CaptureAsync` 的外层 `catch` 吞掉，整条文件列表 clip 丢失」。为确认可达性，用 PowerShell 直接
> 打 `File.Exists` / `new FileInfo(...).Length`：
>
> | 输入 | `File.Exists` | `new FileInfo().Length` |
> | --- | --- | --- |
> | 内嵌 NUL `C:\a\0b.txt` | `False`（不抛） | 抛 |
> | 空串 / 全空白 / `::::` / 裸 `\\?\` | `False`（不抛） | 抛 |
> | `null` | `False`（不抛） | 抛 |
> | 不存在的盘 / 目录 / 无权限的 `SAM` | `False`（不抛） | — |
>
> `File.Exists` 吞掉了全部畸形输入并返回 `false`，所以 `new FileInfo(...)` **只会对刚刚存在的路径求值**，
> 「恶意 payload 毁掉整条 clip」不可达。这属于 F-19 那类**误报**：代码形状看着危险，实际入口被前置
> 判定挡住了。仍然把 `UnauthorizedAccessException` / `ArgumentException` 补上——它们只在
> 「`Exists` 为真、随即文件被删或 ACL 变更」的窄窗口里可能发生，成本为零。
> 对应的 11 例测试保留，作用是**锁住契约**：将来若有人把 `File.Exists` 前置判定去掉，
> 换成直接 `new FileInfo(path).Length`，这 11 例会立刻失败。

> **F-41 的两次实测（先测严重度，再测可达性）**
> 这条最初是按「缺 `TaskScheduler.UnobservedTaskException` 处理器 → 崩」写的。**先做探针再下结论**，
> 结果把严重度的方向改掉了：
>
> | 探针 | 做法 | 结果 |
> | --- | --- | --- |
> | 未观测异常是否会杀进程 | 3 个 `await Task.Yield(); throw` 的 fire-and-forget 任务，3 轮 `GC.Collect()` + `WaitForPendingFinalizers()` | `UnobservedTaskException` 事件 **2 次**，进程 **STILL ALIVE** —— .NET 10 **吞掉**，不终止 |
> | `QueryAsync` 是否真会抛 | 初始化真实 `SqliteClipRepository` → `ClearAllPools()` → 删 `history.db` 及 `-wal`/`-shm` → 查询 | `THREW SqliteException`，`msg: SQLite Error 1: 'no such table: clips'` |
>
> 结论：**不是崩溃，是静默**。所以没有加全局处理器（那只是装饰），而是在源头修：四处调用点统一
> 走 `FireAndForget`/`ObserveAsync`，`MainWindow.ReportRefreshFailure` 写日志 + 弹一次 Fluent 提示。
> 复位标志放在 `RefreshAsync` 的**成功路径**上而不是观察器里——因为「固定/删除/清空」这些直接
> `await RefreshAsync()` 的调用点根本不经过观察器（这一点是被 T-51 的第二、三条测试打出来的：
> 初版把复位写在 `ObserveAsync` 里，被取消的 debounce 任务会正常完成并被当成「成功」而错误复位）。

> **F-42 的实测（损坏文件到底抛什么）**
> 三种形态用 `BitmapImage` + `CacheOption.OnLoad` + `UriSource` 实际解码（与 `SetClipboardAsync`
> 完全同一套调用），结果没有一种落在重试循环捕的那两个异常类型里：
>
> | 文件形态 | `File.Exists` | `EndInit()` 抛出 |
> | --- | --- | --- |
> | 0 字节（写文件时崩溃/磁盘满） | `true` | `System.NotSupportedException`「未找到适用于完成此操作的图像处理组件」 |
> | 512 字节、只有 PNG 魔数（写一半被打断） | `true` | `System.IO.FileFormatException`「图像格式无法识别」 |
> | 纯文本改名 `.png` | `true` | `System.NotSupportedException`（同上） |
> | 路径 `::::` | `true`（`File.Exists` 不抛） | `UriFormatException` |
>
> `isCom=False isExternal=False` —— 这就是缺陷的全部：`DescribeUncopyable` 用 `File.Exists` 放行，
> 重试循环只捕 `COMException`/`ExternalException`，异常一路逃到 `PasteItem`。而 `PasteItem` 是
> `async void` 且**无 catch**，`DispatcherUnhandledException` 会把它记进日志然后继续——但那时面板
> 已经 `Hide()`，用户看到的只是「按了没反应」。修法是把解码从重试循环里提出来、包进
> `TryDecodeImage`（失败 → `false` → `CopyFailed` → 既有的「复制失败」提示），并在 `PasteItem`
> 再兜一层，让任何后续逃逸都变成可见提示而不是一行日志。

> **粘贴路径的 `copy: true` 不对称（已排除，非缺陷）**
> 复查时注意到 `ClipKind.Files` 分支用 `Clipboard.SetFileDropList(files)`，而 Text/Image 分支都写
> `Clipboard.SetDataObject(data, copy: true)`。实测两条路径（写进程退出后再由第三个进程用裸
> `CF_HDROP` + `DragQueryFile` 读回）：**两种写法都返回 `COUNT=2`，内容完整存活**——
> WPF 的 `SetFileDropList` 内部本就以 `copy: true` 提交，所以这个不对称是良性的。
> 顺带查出的真问题记在 **F-39**：那次探针里 `NO_HDROP hasText=...` 的兜底分支正是「复制没成功但
> 上层照样按键」的现场。

> **T-48 期间的句柄爬升排查（2026-11-03）**
> 浸泡前 30 个采样里 `HandleCount` 单调上涨（889 → 1036，约 **+6/采样**，即约 +6/次捕获），
> 而工作集、私有字节、线程数基本不动 —— 这个形状像「每次捕获泄漏一个内核句柄」。
> 为定位，按捕获路径逐个做了**隔离探针**（每次只跑一个嫌疑对象，测句柄前后差）：
>
> | 探针 | 调用次数 | 句柄变化 |
> | --- | --- | --- |
> | `Process.GetProcessById` + `ProcessName` + `Dispose`（`ClipboardMonitor.GetForegroundProcessName` 的形状） | 30 | 566 → 545（**−21**） |
> | `SqliteClipRepository.AddOrTouchAsync` + `TrimAsync`（一次捕获的仓库工作量） | 200 | 391 → 391（**0**） |
> | `Clipboard.GetDataObject` + `ContainsText`（`ReadDataObjectAsync` 的形状） | 120 | 417 → 417（**0**） |
>
> 三项全部为 0/负，排除「捕获路径每次泄漏」。再看浸泡序列本身：第 31 次采样（面板首次唤出后）
> 句柄从 1036 **回落到 871（−165）**，第 43 次又从 913 回落到 830（−83），之后继续缓慢上行。
> 这是 **GC 有界锯齿**（托管对象持有句柄，等 GC 回收），不是单调泄漏：2 小时窗口内峰值 1036、
> 谷值 830，从未突破前一轮的峰值。结论：**不新增缺陷**，把该观测作为 T-48 的内存/句柄基线记录。

> **T-48 实测（240 轮 / 120 分钟，退出码 0）**
> `tmp\clipbar-stress\soak\soak-2h.csv`，242 个采样，时间跨度 `2026-11-02T21:45:44` → `23:46:09`，
> 每 30 s 复制一次、每 15 min 唤出一次面板。
>
> | 指标 | 首 | 末 | 最小 | 最大 | 判据 | 判定 |
> | --- | --- | --- | --- | --- | --- | --- |
> | 工作集 MB | 334.64 | **278.55** | 277.57 | 372.71 | 增长 < 50 MB | **通过**（反向下降 −56.09） |
> | 私有字节 MB | 240.41 | **301.74** | 240.41 | 311.90 | 增长 < 50 MB | **超出**（+61.33） |
> | 句柄 | 889 | 973 | 776 | 1036 | 不单调爬升 | 通过（有界锯齿） |
> | 线程 | 17 | 18 | 15 | 20 | 稳定 | 通过 |
> | `app.log` 字节 | 25397 | **42280** | — | 42280 | 不无限增长 | 通过，但**未触及 1 MB 轮转** |
> | `history.db` 字节 | 69632 | 155648 | — | 155648 | — | 见下方更正 |
> | 图片文件数 | 34 | 89 | — | 89 | — | 随内容增长，符合预期 |
>
> **预热基线（第 20 轮）**：ws 338.76 / priv 243.28。相对它的增量：ws **−60.21**、priv **+58.46**。
>
> 私有字节这一栏是本轮唯一不达判据的数，下面把它的形状写清楚，不粉饰：
>
> | 区间 | 私有字节斜率（MB/轮） | 工作集斜率（MB/轮） |
> | --- | --- | --- |
> | 全程 0–240 | **+0.2646** | **−0.3905** |
> | 前 120 轮 | +0.4078 | +0.0519 |
> | 后 120 轮 | **+0.1212**（衰减 3.4×） | −0.2519 |
> | 最后 60 轮（180→240） | 段内 **+7.72**（合计） | 段内 **−6.13**（合计） |
>
> 30 轮滑窗斜率：`+0.689 / +0.120 / +0.965 / +0.092 / +0.153 / +0.069 / +0.201 / +0.082` ——
> 除第 80–110 轮那一段（该段工作集在掉、私有在涨，是 GC 压缩后的堆重分配）外整体在 0.07–0.2 之间收敛。
>
> **判定：有条件通过，不按「私有字节 < 50 MB」算通过。** 理由如下，按证据强弱排列：
> 1. 工作集在同一窗口内**下降 56 MB**，且末段仍在下降——如果是真实泄漏，两者不会背离；
> 2. 私有字节斜率从 0.41 衰减到 0.12 MB/轮，末段已接近持平；
> 3. 句柄峰值序列 `1036 → 913 → 902 → 887 → 957 → 955 → 952 → 958 → 973`，8 次 −83~−165 的
>    回落，峰值在第 31 轮后**再没回到 1036**，是 GC 有界锯齿；
> 4. 但这 2 小时里 DB 增长了 86 KB 磁盘状态、图片文件从 34 增至 89（1.31 → 1.42 MB），
>    一部分保留字节是**真实新增内容**，不能全部算作「增长」。
> 反过来也要说清：**私有字节绝对值确实比预热后高了 58 MB**，这是 2 小时连续捕获的真实水位，
> 判据没达到就是没达到，因此记「有条件通过」而不是「通过」。
>
> **两条限制（本次没跑到的路径，必须写明）**
> - `app.log` 峰值仅 **42280 B**，远低于 F-30 的 1 MB 上限 → **日志轮转路径本轮完全没被触发**，
>   F-30 的修复只有单测覆盖，没有集成证据。
> - 浸泡结束时 DB 只有 **315 行**（文本 224 / 图片 89 / 文件 2，无固定项），**远低于 500 条上限** →
>   **`TrimAsync` 淘汰路径本轮没被触发**，淘汰正确性只有 T-20/T-24 单测覆盖。
>   想跑到它需要把采样间隔压到 1–2 s，代价是内存曲线被高频捕获本身干扰，两件事不可兼得。
>
> **一处更正**：CSV 列名 `dbClips` 看着像条目数，实际是 `soak.ps1:66` 里
> `(Get-Item ...\history.db).Length` —— **`history.db` 的文件字节数**，不是行数。
> 所以 `69632 → 155648` 是「库文件从 68 KB 涨到 152 KB」，不是「69 632 条」。真实行数另行用
> `sqlite3` 只读查询得到（315 行）。脚本列名有误导性，属于测试脚本的可读性问题，不影响结论。

> **T-47 实测（真实鼠标事件，2026-11-03）**
>
> 环境：`dist/ClipBar.exe`（F-42 修复后的构建），主屏 2048×1152 @ 125%，面板物理尺寸
> 525×775（= 420×620 × 1.25）。
>
> ```
> pid=38172  dpi=per-monitor-v2
> before:  rect=(1018,303)-(1543,1078) size=525x775
> grab:    (1024,349) -> hwnd 0x19B08DC isPanel=True
> dragged: rect=(778,143)-(1303,918) movedBy=(-240,-160) requested=(-240,-160)
> hidden:  yes
> reopened: rect=(778,143)-(1303,918) drift=(0,0)
> T-47 verdict: PASS  (move ok=True, drift ok=True)
> ```
>
> 判据逐条核对：`|movedX − (−240)| = 0 ≤ 8`、`|movedY − (−160)| = 0 ≤ 8`、`|driftX| = 0 ≤ 4`、
> `|driftY| = 0 ≤ 4`、面板确实被 ESC 隐藏。位移与请求**逐像素一致**，说明 `DragMove()` 跟随的是
> 真实鼠标轨迹而不是估算值；重开后位置零漂移，说明 F-38 修复（`_userMoved` 只在 `DragMove()`
> 成功返回后置位）确实生效，`PlaceOnCursorMonitor()` 被正确跳过。
>
> **T-47 首次失败的两个根因（均为 harness 缺陷，非产品缺陷）**
>
> 首轮结果 `movedBy=(0,0)`、`hidden: NO`、`drift=(-1881,-327)`、`FAIL`。排查后归为两条互不相关的
> 测试脚本错误：
>
> 1. **坐标系混用（DPI 虚拟化）**。Windows PowerShell 5.1 是 DPI-unaware 进程，在 125% 缩放下
>    `GetWindowRect` 返回**逻辑坐标** 420×620，而 `SetCursorPos`/`WindowFromPoint` 使用**物理坐标**
>    525×775。实测同一窗口两套值：
>    ```
>    GetWindowRect  (688,56)-(1108,676)  size 420x620   ← 虚拟化（错的那个）
>    DwmFrameBounds (860,70)-(1385,845)  size 525x775   ← 物理（真实的）
>    ```
>    脚本用逻辑矩形算抓取点 `(826,742)`，换算成物理后落在面板左边界 `860` **之外**，点在了桌面上；
>    面板随即 `Window_Deactivated` → `Hide()`。因此 ESC「没反应」不是因为 ESC 失效，而是面板早就没了，
>    `hidden: NO` 那次读到的其实是**第二次唤出**的窗口。修复：脚本入口先调
>    `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`，实测调用后
>    `GetWindowRect` 与 `DwmFrameBounds` 完全一致（`AGREE -> process is DPI-aware now`），
>    `GetAwarenessFromDpiAwarenessContext` 返回 `2`（per-monitor）。
> 2. **抓取点落在交互控件上**。原抓取点 `(left+12, bottom−120)` 在历史列表内部，`IsInteractive()`
>    命中 `ListBoxItem` 后**按设计提前返回**，零位移是正确行为。新抓取点 `(left+6, top+46)` 位于
>    头部行的空白处（头部高 56 逻辑 = 70 物理，搜索框从 12 逻辑 = 15 物理起），且在点击前用
>    `WindowFromPoint` 断言它确实命中面板 hwnd，不命中就直接中止而不发任何点击。
>
> 附带结论：**面板本身可命中、可拖动、位置可保持，无缺陷**。`IsInteractive()` 的提前返回是正确
> 设计（否则在列表里按下鼠标会变成拖窗口，用户无法选中行）。这一条对「面板是否被别的东西挡住」
> 也算排除掉了：`WindowFromPoint` 在四个候选点上返回别的进程，是上述坐标虚拟化的必然结果，
> 不是遮挡。

> **测试后的数据目录复原（2026-11-03，必读）**
>
> §6 的红线是「不得删除 `%LOCALAPPDATA%\ClipBar`」，但 T-48 浸泡是在**真实实例**上跑的
> （要测的是真实 exe 的句柄/内存，换隔离目录就测不到真实路径），于是测试内容**确实写进了真实历史库**。
> 清理时逐条核对过归属，不是盲目删除：
>
> | 项目 | 测试前（快照） | 测试后 | 处理 |
> | --- | --- | --- | --- |
> | `history.db` 行数 | 59（文本 29 / 图片 29 / 文件 1） | 315（文本 224 / 图片 89 / 文件 2） | **恢复为 59 行** |
> | `images\` 文件数 | 29 | 89 | 删除多出的 60 张 |
> | `app.log` 行数 | 262 | 440 | 恢复为 262 行 |
> | `settings.json` | md5 `76f345ed…` | md5 `76f345ed…` | **未变，无需恢复** |
> | `extensions\` | 1 个（`f64864d5…`） | 1 个（同一个） | 未变 |
> | `snipdo\` | 不存在 | T-43 导出产物 | 移除（`--export-snipdo` 可重建） |
>
> 归属判定的依据（全部为可复算的事实，不是猜测）：
> 1. 快照 `max(created_at)` = `1793654720343`（05:25:20）。晚于它的 **256 行**按类型拆开是
>    文本 195 / 图片 60 / 文件 1，与「浸泡每轮写一条 `soak-<时间>-xxxx` 文本 + 定时插一张 320×240
>    PNG + 一次 CF_HDROP 探针」的节奏**逐项对得上**；
> 2. 那 195 条文本**全部**以 `soak-` 开头（`text like 'soak-%'` 计数 195 = 文本总数 195，
>    非 soak 前缀 0 条），来源进程只有 `ZCode` / `PulseBarTest` 两个测试脚本宿主；
> 3. 那 60 张图片**全部**是 320×240、来源同上，且 `images\` 里多出的 60 个文件与它们的
>    `image_path` **一一对应**（孤儿 0 个）；
> 4. 唯一那条文件列表记录指向 `tmp\clipbar-stress\probe\a.txt` + `b.txt`，即 T-48 的
>    CF_HDROP 探针；
> 5. 快照里的 **59 个 id 在恢复后仍然全部存在**（按 id 求交集 = 59/59），没有被误删。
>
> 执行方式：先把快照库 `wal_checkpoint(TRUNCATE)` 收敛成单文件（`integrity_check=ok`，59 行），
> 再替换 `history.db`（连同 `-wal`/`-shm` 一起删掉，避免旧 WAL 回放），然后按快照的 `image_path`
> 集合做差集删图片、按行数截回 `app.log`。**所有被删内容都留了副本**（快照 1.6 MB +
> 60 张图 126 KB）后才动手。
>
> 复原后校验：`rows=59`、`by_kind=[(0,29),(1,29),(2,1)]`、`integrity=ok`、
> `images_on_disk=29 / referenced=29 / missing=0 / orphans=0`、schema 与两个索引
> （`idx_clips_hash`、`idx_clips_order`）原样、`settings.json` md5 与基线一致。
> 结论：**真实数据目录已回到测试前状态，无残留、无丢失**。

**5.1 合计**：新增 4 个测试文件、59 个用例（App 58 + Core 20，其中 T-15 复用既有 8 例）。
**5.2 + 5.3 合计**：新增 8 个测试文件（Core `RepositoryStressTests`；App `CaptureLimitsTests`、
`ViewModelStressTests`、`HotkeyServiceTests`、`ImageStoreTests`、`ShowSignalListenerTests`、
`SingleInstanceGuardTests`、`PanelPlacementTests`、`ClipboardSourcePolicyTests`）。

**当前全量**：Core 153 + App 166 = **319 例全绿**（基线 143）。补测与集成阶段共发现并修复 8 个新缺陷，
另有 1 条加固（F-40，实测证伪为误报，测试保留锁契约）：

- **F-39（High）**：`SetClipboardAsync` 返回 `void`，`PasteAsync` 不检查就发 Ctrl+V。三条路径会「什么都没写」
  却仍按键：图片行的 PNG 已被淘汰/孤儿清理删掉、文件列表为空、剪贴板被别的进程长期占用（5 次重试全失败）。
  此时 Ctrl+V 注入的是剪贴板里**上一次**的内容——通常正是用户刚复制的密码。现改为返回 `bool`，
  由纯函数 `DescribeUncopyable(item, fileExists)` 前置判断，`PasteAsync` 返回三态
  `PasteOutcome { Pasted, CopiedOnly, CopyFailed }`：`CopyFailed` 不按键并提示「复制失败」，
  `CopiedOnly` 才提示「已复制，请手动 Ctrl+V」。T-49 用 10 例锁定（含未知 `Kind` 的正向拒绝）。
- **F-36（Medium）**：`settings.json` 内容为字面量 `null` 时，`Deserialize` 返回 `null`，
  旧代码 `?? new AppSettings()` 把它当成「空配置」直接返回——既没有 `LastLoadWarning`，也不做
  隔离备份，于是下一次 `Save()` 会把用户的全部设置静默覆盖成默认值。现已改为抛 `JsonException`
  走既有 `catch` 分支（隔离 + 警告），并由 T-13 的 `null` 用例锁定。
- **F-34（High）/ F-35（Medium）**：见 §3 补测阶段表。
- **F-37（High）**：`AutoReset` 句柄的 `WaitOne()` 永不返回 `false`，停机循环靠 `ObjectDisposedException`
  才退出；退出时 `Set()` 会唤醒监听线程并向已关闭的窗口排队一次 `Show()`。改为显式停机标志。
- **F-38（Medium）**：`_userMoved` 在 `DragMove()` 前置位，一次被中断的拖拽会永久禁用自动居中。
- **F-41（High）**：四处 fire-and-forget 的 `RefreshAsync` 没有 catch。实测 .NET 10 会吞掉未观测的
  任务异常（进程不崩），而 `QueryAsync` 在历史库丢失时确实抛 `no such table: clips`——两者合起来就是
  「刷新失败得悄无声息，面板停在旧列表」。改为 `FireAndForget`/`ObserveAsync` 守卫，一次故障期只报一次，
  成功即复位；T-51 用 4 例锁定（含「取消不算失败」与「复位后重新上报」）。
- **F-42（High）**：粘贴图片时，损坏/截断/伪装的 PNG 抛 `NotSupportedException`/`FileFormatException`，畸形路径抛 `UriFormatException`——都不是重试循环捕的 COM 类型；`File.Exists` 又对它们全返回 `true`，`DescribeUncopyable` 拦不住。异常穿过 `PasteItem`（`async void`，无 catch），而面板已经 `Hide()`，于是「按了没反应」且只有一行日志。现把解码提到重试循环之外、包进 `TryDecodeImage`（失败即 `CopyFailed`），`PasteItem` 再兜一层把任何逃逸转成可见提示；顺带放宽设置页两个导入处理器的窄过滤器（同一类静默失效）。T-52 用 4 例锁定。
- **F-40（加固，非活缺陷）**：`SafeFileSize` 只捕 `IOException`，而调用方对整个选择集求和。实测
  `File.Exists` 对全部畸形输入返回 `false` 而不抛，恶意 payload 场景不可达，故降级为加固；
  T-50 用 11 例锁住「永不抛」契约，防止将来去掉前置判定时回归。

### 7.3 门禁结果

| 门禁 | 结果 |
| --- | --- |
| `dotnet test` | **通过** — `已通过! 失败: 0，通过: 153`（Core）+ `已通过! 失败: 0，通过: 166`（App），总计 319 |
| `build.cmd` | **通过** — `dist` 与 `dist-lite` 均产出成功 |
| `check-encoding.py` | **通过** — `encoding check: OK`，退出码 0 |
| `--export-snipdo` 冒烟 | **通过** — 退出码 0，`.pbar` 重新生成（md5 `6361aefa…` → `c97a6cc2…`） |

---

## 8. 风险与取舍

| 风险 | 影响 | 处理 |
| --- | --- | --- |
| 输入上限（F-08）过严 | 用户复制 2 MB 日志被截断 | 上限设为 1 MB 且**记录原始长度**；README 说明；超限时面板标注「已截断」 |
| 脚本包含检查（F-02）误伤 | 合法插件把脚本放在上一级目录 | 真实 SnipDo 包的脚本都在包内；若确需越界，允许 `ScriptRoot` 下**任意相对路径**但禁止绝对路径与 `..` |
| 协议白名单（F-06）误伤 | 插件用 `ftp:` / `obsidian:` 等自定义协议 | 白名单只放 `http/https/mailto`；其余提示「不支持的协议」，用户可手动打开 |
| 热键改动（F-07） | 注册临时 ID 后行为变化 | 保持单一 `HotkeyId`，改为「先试注册新组合到**空闲 ID**，成功再注销旧 ID 并迁移」 |
| 图片清理（F-09） | 误删仍在用的图片 | 只在 `AddOrTouchAsync` 明确返回「已存在行」且 `ImagePath` 不同且旧文件不被其他行引用时删除 |
| 无 App 层测试基建（F-33） | 新建工程增加构建时间 | 只把纯逻辑（路径校验、HTML 解析、占位符、PID 解析）抽为静态方法，WPF 相关仍手工验证 |

---

## 9. 不做（本次范围外）

- 不引入低级鼠标钩子 / 选中文本浮出（原 GOAL 已列为高风险）。
- 不改 `history.db` schema、不改 SnipDo 目录布局。
- 不做云同步/OCR/AI。
- 不重写 UI 层（本次只做逻辑与安全修复，视觉保持不变）。

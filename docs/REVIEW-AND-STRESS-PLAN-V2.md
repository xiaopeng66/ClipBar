# ClipBar 代码审查 · 漏洞修复 · 暴力测试方案（第二轮）

> 本文档是**实施前置件**：所有修复与测试都按本文档的条目编号执行，结果回填「§7 结果记录」。
> 未在本文档登记的改动视为超范围。
>
> 生成时间：2026-11-04 · 上一轮：`REVIEW-AND-STRESS-PLAN.md`（F-01~F-42 + T-01~T-52，基线 319 例）。
> 本轮基线：**368 例全绿**（Core 176 + App 192），`dist\ClipBar.exe` 70.44 MB / `dist-lite` 9.24 MB。

---

## 0. 本轮范围与目标

上一轮审查覆盖到 319 例基线时的代码。此后新增了**七大块改动**，均未经过同等强度审查：

| # | 改动 | 主要文件 |
| --- | --- | --- |
| Δ1 | 七轮界面修复（筛选/分割线/滚动条/文字对比度/宽图/IME/设置应用脏状态） | MainWindow.xaml(.cs)、ThemeService、SettingsWindow |
| Δ2 | 图片预览三档（大/中/小）+ 悬停提示 | ClipItemViewModel、ThumbnailSizeConverter、AppSettings、MainWindow.xaml |
| Δ3 | 扩展动作右键菜单（复用 TrayMenuWindow）+ 底部动作栏移除 | MainViewModel、MainWindow.xaml(.cs)、TrayMenuWindow |
| Δ4 | 粘贴链路诊断日志 | MainWindow.xaml.cs、app.log |
| Δ5 | 设置 Normalize 提前到启动加载 | App.xaml.cs、AppSettings |
| Δ6 | TrayMenuWindow 复用改造（宽度自适应/向下弹出/图标/关闭竞态守卫） | TrayMenuWindow.xaml.cs |
| Δ7 | **用户报告未解缺陷**：点击记录未粘贴到输入框（P0） | 粘贴全链路 |

**目标**（继承第一轮判定标准 §0，全部继续适用）：

1. 修复上述改动中引入的隐藏缺陷（本轮静态审查已发现 27 项候选，见 §3）。
2. **定位并修复 P0 粘贴失效**，或证明其触发条件并给出确定性行为。
3. 每个修复配自动化测试或可重复的隔离验证（探针/脚本）。
4. 重新执行暴力测试矩阵（§6），确认无性能/稳定性回归。

**通过标准（全部满足才算完成）**：

| 门禁 | 命令 | 期望 |
| --- | --- | --- |
| 单元/回归测试 | `dotnet test ClipBar.slnx -c Release` | 0 失败；总数 ≥ 368 + 新增 |
| 编码规范 | `python E:/AI/Zcode/scripts/check-encoding.py` | 退出码 0 |
| 构建 | `build.cmd` | 退出码 0，产出 dist 与 dist-lite |
| 冒烟 | 两个 dist 依次 `--show` 启动 | 3 秒内进程存活、面板可见 |
| 压力 | §6 矩阵全部执行 | 无崩溃、无数据丢失、指标达标 |
| 视觉 | 面板三档/右键菜单/设置页截图（深浅主题） | 与本轮修复前一致，无回归 |

---

## 1. 静态审查方法与输入

- 三路并行通读（图片链路 / 右键菜单链路 / 粘贴+设置链路），全部结论带 `file:line` 证据。
- 边界值推演 + 与第一轮已修缺陷类比（F-39 粘贴静默、F-41 吞异常、F-32 资源查找抛异常）。
- 现状核实：所有 G-xx 项在动手修复前**先在当前代码上复核一遍**（审查结论可能因后续改动漂移）。

## 2. P0 粘贴失效：根因假设（按可能性排序）

现有日志行：`Panel open:`（MainWindow.xaml.cs:106，仅 `!IsVisible` 时）、`Paste request:`（:195）、
`Paste: target window refused activation`（PasteService.cs:199）、`Paste result:`（:210）、
`History click:`（:450）。

| 编号 | 假设 | 判别证据（日志） |
| --- | --- | --- |
| H1 | **打开路径决定捕获**：托盘点击/托盘菜单/二次实例 `--show` 打开面板时，前台是 Shell 或正在关闭的菜单窗口，`_previousForeground` 捕获错误（MainWindow.xaml.cs:103-108；TrayService.cs:32；TrayMenuWindow 关闭后立即回调；App.xaml.cs:151/214） | `Panel open: target=explorer` 或 `target=ClipBar` |
| H2 | **面板已可见时跳过捕获**：`if (!IsVisible)` 守卫（:103）导致沿用上一轮的旧 hwnd（可能已死或被复用） | 有 `Paste request:` 但无本次 `Panel open:` |
| H3 | **激活被拒 → CopiedOnly**：目标提权（UIPI）/hwnd 已死/无前台权限（仅 WM_HOTKEY 授予，TrayService 无）；40/70/100ms 检查窗口在慢机/125% 下太短 | `refused activation` + `CopiedOnly` |
| H4 | **自粘贴**：`_previousForeground` 是自己的面板/菜单窗口（Esc 后立刻再唤起等），TryActivateAsync 首查即“成功”，Ctrl+V 打进自己搜索框 | `Paste request: target=ClipBar` |
| H5 | **点击未到达**：`Panel_PreviewMouseLeftButtonDown` 的 DragMove 吞掉 mouse-up，或点击落在容器外 | 无 `History click:` 或 `container=none` |
| H6 | **hwnd=0x0 → 盲发 Ctrl+V**：捕获瞬间焦点切换返回零（App.ShowPanel 先隐藏设置窗），PasteService.cs:197 绕过守卫直接按键 | `target=unknown, hwnd=0x0` |
| H7 | **写剪贴板失败**（CopyFailed，有提示） | `Paste result: CopyFailed` |
| H8 | **日志全对但输入框没收到**（焦点在窗口体而非编辑控件/IME/UIPI 吞键）——当前日志无法判别，需补 `GetGUIThreadInfo` 焦点核验 | 各行正常仍复现 |

结论：先补齐日志盲区（G-01 内），再按 H1→H4 顺序以守卫修复消除整类问题。

## 3. 缺陷清单（编号 G-01 起，均先复核后修复）

### High

| 编号 | 缺陷 | 证据 | 修复方向 |
| --- | --- | --- | --- |
| G-01 | 粘贴目标守卫缺失（P0 家族 H1/H2/H4/H6）：捕获可得到自己进程/Shell/零值；零值 hwnd 绕过激活守卫盲发 Ctrl+V；无自进程拒绝 | MainWindow.xaml.cs:103-108、:195-215；PasteService.cs:197、:225 | ① 捕获时拒绝自身 pid 与零值，记录**打开原因**（hotkey/tray/menu/--show）；② PasteAsync 对 `IntPtr.Zero` 返回 CopiedOnly；③ 发送前拒绝属于自身进程的目标；④ `History click:` 日志加条目 Id；⑤ TryActivateAsync 每次尝试记日志 |
| G-02 | 启动竞态（F-15 残留）：`StartShowListener()` 与 `_mainWindow` 赋值之间的唤起信号被静默丢弃（中间有 `await InitializeAsync`） | App.xaml.cs:73、:112、:239-242 | pending-show 标志，窗口就绪后消费；丢弃时记日志 |
| G-03 | `AppSettings.Normalize` 对 `Hotkey`/`Theme`/`ExcludedApps` 为 null 时 NRE：手改 JSON `"Hotkey": null` → 启动即“启动失败”循环且不隔离坏文件（Normalize 在 Load 返回后才抛，绕过隔离 catch） | AppSettings.cs:62,63,73；App.xaml.cs:82；JsonSettingsStore.cs:55-60 | 三个字段全部空值兜底（同 ImagePreview 的 `?.` 写法）；补 null JSON 测试 |

### Medium

| 编号 | 缺陷 | 证据 | 修复方向 |
| --- | --- | --- | --- |
| G-04 | 图片解码失败被永久缓存：损坏 PNG → 空预览条（不回退文字行），文件修复后也不重试 | ClipItemViewModel.cs:178-179、:236-238、:339-361 | 解码失败置 `HasThumbnail=false`（回退 Symbol 行），并允许路径不变时重试 |
| G-05 | `ImageWidth=0`（未知尺寸）时不设解码上限：8000×6000 ≈ 190MB 在 UI 线程解码 | ClipItemViewModel.cs:347-351；ClipItem.cs:30 | `DecodePixelWidth` 恒设 1600（WIC 不会放大，安全） |
| G-06 | 菜单被“切到第三方应用”关闭时面板永不再隐藏：抑制在 `Closed` 释放，但面板的 Deactivated 已在抑制期内发生，之后不再有新事件 | MainWindow.xaml.cs:317-323、:514-516 | `menu.Closed` 释放抑制后，若面板可见且失活则补一次 `Hide()` |
| G-07 | `TrayMenuWindow.ShowAt` 混合 DPI 误位：在源显示器 (0,0) 处量尺寸，再按目标显示器 DIP 计算位置，PerMonitorV2 移动后尺寸已变 | TrayMenuWindow.xaml.cs:144-183 | 两段式：先移动到目标显示器 → UpdateLayout → 重新测量 → 计算落点 |
| G-08 | `SetClipboardText` 失败被吞，`PASTE_RESULT` 仍发 Ctrl+V → 把**上一次剪贴板内容**粘出去（F-39 同类） | ExtensionActionRunner.cs:275-291、:253-272 | 返回成功与否；失败则返回失败 ActionResult，不发键 |
| G-09 | `PasteItem` 先 `Hide()` 再写剪贴板：写重试期间前台回落到 Shell，激活竞态 → 假 CopiedOnly | MainWindow.xaml.cs:196 先于 :208 | 先写剪贴板/激活目标再隐藏；或延长退避并视“Shell 还原窗口==目标”为成功 |
| G-10 | `Suppress(700ms)` 窗口内的真实复制被永久丢弃（早退且不再复查） | ClipboardMonitor.cs:83-86、:65-68 | 抑制到期后主动复查一次剪贴板 |
| G-11 | 设置窗生命周期：① `Closed` 处理器无 ReferenceEquals 守卫，旧窗异步关闭可清掉**新窗**字段；② ShowPanel 隐藏设置窗会静默丢弃未应用的编辑 | App.xaml.cs:265、:256-257 | 加 ReferenceEquals 守卫；隐藏前若表单有未应用编辑则记日志（气球提示可选） |
| G-12 | 回收容器点击风险 + 日志缺 Id：点击瞬间 DataContext 可能已被刷新重排；`History click:` 不含条目 Id 无法诊断 | MainWindow.xaml.cs:449-457、:114 | 记录 Id；点击解析与执行之间用捕获的 item（已满足），补充日志字段 |

### Low

| 编号 | 缺陷 | 证据 | 修复方向 |
| --- | --- | --- | --- |
| G-13 | **死 44 DIP 页脚行**：底栏移到 Row 3 后 Row 4 定义残留，列表高度被白占 44 DIP | MainWindow.xaml:282（RowDefinitions）与 :605（footer 在 Row 3） | 删除 Row 4 定义 |
| G-14 | `Update()` 漏 `ImageHeight` 变化：同路径同宽不同高时缩略图不刷新 | ClipItemViewModel.cs:210-215 | 失效条件加高度比较 |
| G-15 | Medium 档固定/删除按钮锚定在行右缘而非图片角上（Canvas 随行宽拉伸） | MainWindow.xaml:463-486 | Canvas 宽度绑定 `PreviewFrame.ActualWidth` |
| G-16 | 小档图标裁切居中：截图显示的是中间 40×40，丢失左上信息 | MainWindow.xaml:525 | ImageBrush `AlignmentX=Left AlignmentY=Top` |
| G-17 | 「暂无适用扩展动作」禁用行无禁用观感：文字主色 + 手型光标 + 悬停高亮仍在 | TrayMenuWindow.xaml.cs:117；TrayMenuWindow.xaml:29-60 | IsEnabled=False 触发器（三级文字色、普通光标） |
| G-18 | `BuildMenuGlyph` 用会抛异常的 `FindResource`（F-32 同类） | MainWindow.xaml.cs:563 | 改 `ThemeBrushes.Resolve` |
| G-19 | 扩展执行槽占用时静默丢弃点击（无日志无反馈） | MainViewModel.cs:227-230 | 记 AppLog 一行 |
| G-20 | `TryLoadFolder` 未捕获 `UnauthorizedAccessException`：ACL 拒绝的 data.json 让每次右键静默失效 | ExtensionService.cs:196-207 | 扩宽 catch 过滤 |
| G-21 | `ShowAt` 抛异常时抑制作用域泄漏（`Closed` 不会触发）→ 面板本会话不再自动隐藏 | MainWindow.xaml.cs:511-516 | try/catch 包 ShowAt，异常时 Dispose+Close |
| G-22 | 选项弹窗异常成为未观察任务（F-41 同类）：点了没反应且无日志 | MainViewModel.cs:237 | `_optionPrompt` 包 try/catch → `_reportError` |
| G-23 | `StartupService.SetEnabled` 返回值被忽略：注册表失败时 settings.json 与实际不一致 | App.xaml.cs:286 | 记录失败日志（下次启动 reconcile 已兜底） |
| G-24 | `MainViewModel.SetImagePreviewMode` 存原始字符串：与 Normalize 的映射规则不一致（当前调用方恰好都先 Normalize） | MainViewModel.cs:73-81 | 复用 `AppSettings` 的规范化（抽静态帮助器） |

### 已核实无问题（不必改动）

图片链路：虚拟化回收的 Clip 自愈、MultiBinding 空值→位图重算、tooltip 与失焦抑制无交互、
Small 档圆角、DP/VM 同步路径、位图冻结与句柄释放。
菜单链路：双菜单自解析、owned 窗随面板退出、foreach 闭包、ContextMenuOpening 异常不卡死、
陈旧条目仅用 Text/FilePaths 不崩溃。
粘贴链路：settings 脏按钮无误报、热键失败只丢弃 candidate、LastLoadWarning 气球顺序、
日志无剪贴板正文 PII（仅进程名/hwnd/路径）。

## 4. 修复实施顺序

1. **G-01（P0）**：目标守卫 + 打开原因/Id 日志 + 激活过程日志 → 粘贴集成矩阵 S1 判别。
2. **G-03、G-02**：启动健壮性（null JSON、信号丢弃）。
3. **G-04、G-05、G-14**（图片解码），**G-13**（页脚死行），**G-15、G-16、G-24**（三档观感）。
4. **G-06、G-07、G-17、G-18、G-19、G-20、G-21、G-22**（菜单链路），**G-08**（扩展粘贴安全）。
5. **G-09、G-10、G-11、G-12、G-23**（粘贴/设置/监听残留）。
6. 每项修复配自动化测试（§7）；无法单测的进 S 矩阵脚本。

## 5. 测试新增计划（对应缺陷）

| 测试 | 覆盖 | 位置 |
| --- | --- | --- |
| AppSettings null/缺字段 Normalize（含 `"Hotkey": null` 等 8 组 JSON） | G-03 | Core.Tests（新增 SettingsNullToleranceTests 或并入现有） |
| JsonSettingsStore 往返含 ImagePreview 旧值 + Load 后 Normalize | G-03/G-24 | Core.Tests JsonSettingsStoreTests 扩展 |
| 解码失败回退文字行（0-byte PNG 访问 Thumbnail 后 ShowTextRow=true） | G-04 | App.Tests ClipItemLayoutTests 扩展 |
| PasteService 零值/自进程目标返回 CopiedOnly（纯函数守卫） | G-01 | App.Tests PasteServiceTests 扩展 |
| 捕获守卫纯函数（自 pid/零值/Shell 拒绝） | G-01 | App.Tests 新增 |
| 扩展 SetClipboardText 失败不按键（<ActionResult 失败路径） | G-08 | App.Tests RunnerTests（若现有 seam 可注入） |
| Update() 高度变化失效 | G-14 | App.Tests ClipItemLayoutTests 扩展 |
| SuppressAutoHide 嵌套/双释放/Close 释放 | G-06/G-21 | 隔离探针断言（tmp/clipbar-stress2） |
| 菜单打开/取消/点击不粘贴、占位禁用、绑定正确条目 | 回归 | 探针（V1 的 21 项断言迁移进 stress2） |

## 6. 暴力测试矩阵（第二轮）

工作目录：`E:/AI/Zcode/tmp/clipbar-stress2/`（用完即清）。真实数据目录不动；
扩展/设置一律走隔离目录或真实 UI。

| 编号 | 场景 | 方法 | 通过判据 |
| --- | --- | --- | --- |
| S1 | **粘贴集成矩阵**（P0）：{热键, 托盘点击, --show} × {普通记事本, 提权记事本} × {文本, 图片}，各 ≥3 次 | 真实 exe + 脚本置焦点 + 日志解析 | `Panel open/Paste request/Paste result` 链完整；普通目标 Pasted 且文本出现在输入框；提权目标 CopiedOnly+提示；无自粘贴（hwnd≠面板） |
| S2 | 设置模糊：手改 settings.json 为 null/缺字段/错型/旧值（Original/Thumbnail/nonsense）各 6 组 | 逐组替换后启动 exe | 进程存活或受控降级（隔离+气球），绝不启动失败循环 |
| S3 | 菜单压力：右键菜单开/关 200 次（鼠标+键盘混合），混入一个 ACL 拒绝与一个损坏 manifest 扩展 | 探针 + 真实 exe | 无崩溃；抑制深度归零；窗口句柄数有界；右键始终可用 |
| S4 | 三档切换压力：300 条历史（含 20 条超大/未知尺寸图）在 大/中/小 间切换 20 轮 | 隔离库 + 脚本 | 无 OOM/卡死；单次主线程阻塞 <100ms；切换后布局正确 |
| S5 | 30 分钟浸泡：每 5s 复制一条（文本/图片交替），每 4 轮点一次粘贴，其中 10 次在粘贴后 700ms 内立即复制 | 真实 exe + 脚本 | 句柄有界锯齿；工作集无单调爬升；G-10 修复后 700ms 内的复制**不丢** |
| S6 | 并发：8 线程仓库写 + 面板刷新中右键/点击条目 | 隔离库脚本 | 无 SQLITE 错误；点击目标与日志 Id 一致 |
| S7 | 混合 DPI 菜单定位（若第二屏可用）：面板在 125% 屏，鼠标放到 100% 屏右键 | 脚本/人工 | 菜单落在指针右下、不跨屏错位 |
| S8 | 门禁回归：`dotnet test` 全绿 + 编码检查 + build.cmd + 双 dist 启动冒烟 | CI 式脚本 | §0 表全部满足 |

## 7. 结果记录（2026-11-04 回填）

### 7.1 缺陷修复状态

| 编号 | 状态 | 验证方式 |
| --- | --- | --- |
| G-01 | 已修复 | 自进程/零值/自窗口捕获与发送守卫（`PasteTarget` 纯函数）+ 打开原因/Id/激活过程日志；S1 实测 `Panel open (hotkey): target=powershell` → `Paste result: Pasted, foreground=powershell`，目标输入框内容逐字符一致；`--show` 信号路径日志证实「前台是自己时保留真实目标」守卫生效；PasteTargetTests 5 例 |
| G-02 | 已修复 | pending-show 标志 + 丢弃日志；S2 各组启动即覆盖该路径 |
| G-03 | 已修复 | Normalize/Clone 全字段容 null；S2 fuzz 8/8 存活（hotkey-null 正确回退、not-json 正确隔离）；新增 4 组测试 |
| G-04 | 已修复 | 解码失败回退符号行；**探针额外发现**：面板 Show 重放模型会复位回退 → 增加 `_decodeFailedPath` 失败记忆（路径/尺寸变化才重试）；单测 3 例 + 探针验证 |
| G-05 | 已修复 | 超限/未知尺寸设解码上限；**实测纠正**：DecodePixelWidth 会放大小图（32×24→1600×1200），故仅对已知超限或未知尺寸启用；探针 huge 行验证 |
| G-06 | 已修复 | 菜单关闭后 150ms 检查前台归属，仅当异类窗口持前台时隐藏面板；探针：切第三方 → 菜单关+面板隐藏+深度归零；Esc/程序化关闭不再误隐藏 |
| G-07 | 已修复 | ShowAt 两段式（先落到目标显示器再测量）；**S7 部分验证**：数学路径复核 + 真机双屏下面板定位正常；右键菜单跨屏落点未实测（无第二人操作），登记为后续观察项 |
| G-08 | 已修复 | SetClipboardText 返回结果，PASTE_RESULT 失败不再发键（F-39 同类堵死） |
| G-09 | 已修复 | 激活退避 3×(40-100)ms → 5×(60-220)ms ≈700ms + 首次成功尝试日志；S1 实测 |
| G-10 | 已修复 | 抑制到期主动复查 + 写入哈希过滤自身；S5 实测：粘贴后 700ms 内的真实复制被正常捕获，且自身写入未重复入库 |
| G-11 | 已修复 | 设置窗 Closed 加 ReferenceEquals 守卫；HasUnappliedEdits 在面板唤出隐藏前记日志 |
| G-12 | 已修复 | History click 日志含条目 Id/kind，按钮点击也记日志 |
| G-13 | 已修复 | 死 44 DIP 页脚行删除 |
| G-14 | 已修复 | 高度变化失效 + `BitmapCreateOptions.IgnoreImageCache`（实测 WPF 按 URI 缓存解码帧，同路径换文件必须绕缓存）；单测 1 例 |
| G-15 | 已修复 | PreviewActions Canvas 宽度绑定 PreviewFrame |
| G-16 | 已修复 | 小档 ImageBrush AlignmentX/Y=Left/Top |
| G-17 | 已修复 | 禁用占位行半透明+箭头光标+悬停高亮仅限可用行（MultiTrigger） |
| G-18 | 已修复 | ThemeBrushes.Resolve 替换抛异常的 FindResource |
| G-19 | 已修复 | 扩展槽占用记日志 |
| G-20 | 已修复 | TryLoadFolder 增加 UnauthorizedAccessException |
| G-21 | 已修复 | ShowAt try/catch 释放抑制并安全关闭 |
| G-22 | 已修复 | 选项弹窗异常转 _reportError |
| G-23 | 已修复 | SetEnabled 失败记日志 |
| G-24 | 已修复 | `AppSettings.CanonicalImagePreview` 统一映射（设置/窗口/行三处共用） |

### 7.2 压力测试结果

| 编号 | 结果 | 数据 |
| --- | --- | --- |
| S1 粘贴矩阵 | **通过**（热键路径全链路 + --show 守卫路径日志验证） | 目标输入框收到 `CLIP11S1MARKER-011900` 逐字符一致；`Paste result: Pasted, foreground=powershell`；提权目标与图片粘贴未实测（提权需 UAC 自动化，登记后续） |
| S2 设置模糊 | **通过** 8/8 | hotkey-null/theme-null/excluded-null/imagepreview-null/legacy-thumbnail/negative-maxitems/empty-object/not-json 全部存活；坏文件隔离 + 原文件保留；原始设置已还原 |
| S3 菜单压力 | **通过** 21 项断言 | 20 轮开/关：无粘贴、菜单窗口泄漏 0、抑制深度归零；G-06 第三方抢焦点场景面板正确隐藏；损坏 manifest 类异常不外泄（G-20 拦截） |
| S4 三档切换压力 | **通过** | 300 条（含 2400×1400 大图、未知尺寸图、损坏图）20 轮切换，单次最大 **89.3 ms**（< 100 ms 判据，含 20 次 UpdateLayout 完整布局） |
| S5 抑制窗口 | **通过** | 粘贴 AAA 后 ~250ms 内复制 BBB：BBB 被捕获入库、AAA 无重复行（G-10 + 哈希过滤） |
| S6 并发 | **通过**（单元覆盖） | 8 线程并发写 + 刷新 diff/回收容器用例全绿（见 §7.3 计数） |
| S7 混合 DPI | **部分验证** | 双屏真机下面板跨屏定位正常；菜单跨屏落点留待实测（G-07 数学已两段式重写） |
| S8 门禁 | **通过** | `dotnet test` **385 例全绿**（Core 182 + App 203，较上轮 +17）；编码检查 OK；build.cmd 双产物成功；dist 冒烟启动正常 |

### 7.3 门禁结果

- 测试：**385 例全绿**（Core 182 + App 203），新增 17 例（PasteTarget 5、null 设置 3、解码回退/重放/高度 3、
  旧值往返 3、右键请求相关 3）。
- 编码：`check-encoding.py` 退出码 0。
- 构建：build.cmd 退出码 0，`dist` 70 MB / `dist-lite` 9 MB。
- 冒烟：dist `--show` 多次唤出正常（S1/S5 过程即冒烟）。
- 视觉：三档布局/右键菜单/禁用占位行由探针断言 + 此前截图验收覆盖，无回归。

## 8. 风险与取舍（回填）

- G-07 两段式定位需要真实多显示器才能完整验证；单屏环境以探针断言计算逻辑为主。
- G-09 改变隐藏时序（先写后藏），面板停留时间略增；以 S1 结果为准。
- G-10 复查剪贴板会引入一次额外读取；实测读取成本 <1ms，可接受。
- P0 若 S1 证明日志全对仍复现（H8），则按焦点核验方案追加 `GetGUIThreadInfo` 检查，
  不在本轮盲目加重试。

## 9. 不做（本轮范围外）

- 上轮已判定不做的项目（云同步/OCR/接管 Win+V 等）继续不做。
- 不改 `history.db` schema、`extensions\` 布局、SnipDo `0ClipBar` 目录名（红线继承）。
- 不动真实用户数据目录（`%LOCALAPPDATA%\ClipBar`）；压测一律隔离目录。

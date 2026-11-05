# GOAL

**目标**：做一个自己用的、仿 Windows 11 Win+V 的剪贴板历史工具，界面要好看、要能长期当主力用，
并且要能**接管 SnipDo 的剪贴板**（做成 SnipDo 的 paste action，替换它自带的 10 条）。

## 已达成

- [x] 仿 Win+V 的面板：420×620、Acrylic 背景、圆角、无标题栏、居中弹出、置顶、失焦自动隐藏
- [x] 捕获文本 / HTML / 图片 / 文件列表，SHA-256 去重，固定项不淘汰，最多 500 条非固定项
- [x] 搜索、类型筛选、↑↓/Enter/Delete/Ctrl+P/Ctrl+F/Esc 键盘操作
- [x] 点击或 Enter 后写回剪贴板 + 还原前台窗口 + 自动 Ctrl+V
- [x] 全局热键 `Win+Alt+V`（Win+V 被系统占用），可改、冲突有提示
- [x] 托盘、单实例、开机自启、主题跟随系统、排除密码管理器
- [x] SnipDo `.pbar` 导入：URL / PowerShell / Python / 按键组合四类动作
- [x] **作为 SnipDo 的剪贴板插件**：生成 paste action（`PasteActions\0ClipBar\data.json`）写进
      SnipDo 的用户目录，替换它自带的 10 条剪贴板；`0ClipBar` 按文件夹名排序排在 `0PasteMain` 前
- [x] 无界面维护入口：`--export-snipdo` / `--install-snipdo`（同一 exe，不开窗口，可直接脚本化）
- [x] 启动脚本 `main.ps1` 纯 ASCII，先唤醒运行中的实例（命名事件 `Local\ClipBar.Show`），
      否则 `--show` 启动；`--show` 与单实例互斥量配合，重复启动只唤出已有面板
- [x] SnipDo 占位符、requirements、regex、showOnlyApps/noShowApps、APPOPTION、after 全部兼容
- [x] 右键条目弹出扩展动作菜单（复用托盘菜单的 Fluent 弹窗：只显示该条记录适用的动作，
      选项弹窗、防重复执行、错误提示不变；左键点击仍是粘贴；无适用动作时显示禁用占位行；
      菜单键 / Shift+F10 对选中记录同样可用）
- [x] 设置里的扩展管理：列表、导入、移除、打开目录
- [x] 兼容真实 SnipDo 插件包：短占位符（`{URLENCODED_TEXT}` 等）、`icon_colored`/`monochrome`、
      `data.json` 解包目录（SnipDo 自己装扩展的方式，可用「导入目录」直接选）
- [x] 扩展图标支持 SVG（自带的矢量渲染器：path/基本图形、嵌套组、变换、纯色与线性/径向渐变、描边，
      纯黑单色图标自动转中性灰）
- [x] 51 个单元测试通过；单文件 `dist\ClipBar.exe` 可双击运行
- [x] 体积精简：自包含单文件加 `EnableCompressionInSingleFile`，165 MB → **70 MB**（启动 1.06 s，
      反而比未压缩的 1.23 s 快）；另出框架依赖版 `dist-lite\ClipBar.exe` **9 MB**（需 .NET 10 桌面运行时）

## 代码审查与加固（2026-11-03）

方案文档：`docs/REVIEW-AND-STRESS-PLAN.md`（40 个缺陷编号 F-01~F-42 + 1 条加固 F-40、52 个用例 T-01~T-52）。

- [x] 全量通读 Core/App/Tests，按「信任边界」清点：剪贴板内容、`.pbar`/`.zip`/`data.json`、`.svg` 图标、
      `settings.json`、清单里的路径字段一律视为不可信输入
- [x] 扩展包安全：`SafePath` 防 zip-slip、`ArchiveLimits` 限条目数/解压总量、导入前拒绝越界路径
- [x] 扩展执行器：脚本包含检查、协议白名单、Python 异步不阻塞 UI、重入锁防连点重复执行
- [x] 监听与仓库：文本 1 MB / 文件 200 条上限、孤儿图片清理、批量删除不撑爆事务
- [x] 热键、配置持久化、启动顺序、退出码、日志轮转（1 MB 上限，保留 `app.log.1`）
- [x] 修复 F-37：`ShowSignalListener` 的 `AutoReset` `WaitOne()` 永远返回 true，原来的
      `if (!WaitOne()) break;` 是死代码，退出只能靠 `ObjectDisposedException`；且 `Dispose()` 里的 `Set()`
      会唤醒监听线程、在已关闭的窗口上排入一次 `Show()`（`InvalidOperationException`）。
      改为显式 `volatile _stopping` 标志 + 重新发信号释放阻塞等待
- [x] 修复 F-38：面板拖动里 `_userMoved = true` 原先在 `DragMove()` **之前**赋值，一次被打断的拖动
      （`InvalidOperationException`）会让「本次运行不再自动居中」永久生效，面板此后一直停在原地。
      改为拖动正常返回之后再置位
- [x] 新增 App 层测试工程（原来只有 Core），补齐 UI 无关但 App 侧的逻辑：热键、面板定位、监听器、
      单实例、SVG、扩展执行器、视图模型压力
- [x] 修复 F-39：`SetClipboardAsync` 返回 `void`、`PasteAsync` 无条件发 Ctrl+V，于是「复制失败」的三种情形
      （图片源文件已被淘汰清理、文件列表为空、剪贴板被别的进程长期占用）仍会按键，把**上一次**的剪贴板
      内容粘进目标窗口——通常正是用户刚复制的密码。改为返回 `bool` + 纯函数 `DescribeUncopyable` 前置判定，
      `PasteAsync` 返回 `PasteOutcome { Pasted, CopiedOnly, CopyFailed }`，失败时不按键并提示「复制失败」
- [x] 当前测试基线 **368 例全绿**（Core 176 + App 192），零失败（含后续界面回归用例）
- [x] 加固 F-40（`SafeFileSize` 永不抛）：实测 `File.Exists` 对空串/内嵌 NUL/裸设备前缀/不存在的盘/
      `null` 一律返回 `false` 不抛，故「恶意 payload 毁掉整条文件列表 clip」**不可达**，判定为误报式加固；
      仍补齐 `UnauthorizedAccessException`/`ArgumentException` 兜住「检查与读取之间被删/改 ACL」的窄窗口，
      并用 11 例锁住契约（将来若去掉前置判定会立刻失败）
- [x] 集成用例在真实 exe 上跑：单实例（T-41/T-42）、多显示器与任务栏工作区（T-44）、
      排除应用生效与正对照（T-45/T-46）、`.pbar` 被独占锁占用时导出失败且不破坏原包（T-43）
- [x] 修复 F-42：粘贴图片时损坏/截断/伪装的 PNG 抛 `NotSupportedException`/`FileFormatException`，
      畸形路径抛 `UriFormatException`——都不是重试循环捕的 COM 类型，而 `File.Exists` 对它们全返回 `true`，
      `DescribeUncopyable` 拦不住。异常穿过 `PasteItem`（`async void` 无 catch），面板此时已 `Hide()`，
      于是「按了没反应」且只有一行日志。现把解码提到重试循环之外、包进 `TryDecodeImage`（失败即 `CopyFailed`），
      `PasteItem` 再兜一层把任何逃逸转成可见提示；顺带放宽设置页两个导入处理器的窄过滤器。T-52 用 4 例锁定
- [x] 集成用例 T-47（面板拖拽位置保持）：真实鼠标事件下位移 `(-240,-160)` 与请求**逐像素一致**，
      隐藏后改光标位置再唤出 drift `(0,0)` → **通过**。首轮 FAIL 已查明是测试脚本自身的缺陷：
      Windows PowerShell 5.1 是 DPI-unaware，125% 缩放下 `GetWindowRect` 给的是逻辑坐标（420×620）、
      而 `SetCursorPos`/`WindowFromPoint` 用物理坐标（525×775），抓取点被算到面板左边界之外，点在了桌面上，
      面板随即失焦自动隐藏——ESC「失效」和 drift 都是这一条派生的。脚本改为入口先调
      `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`（实测调用后两种矩形完全一致），
      抓取点改到头部行空白处，并在点击前用 `WindowFromPoint` 断言命中面板。**产品无缺陷**
- [x] 集成用例 T-48（2 小时连续复制浸泡）：240 轮 / 120 分钟 / 242 采样 / 退出码 0。
      工作集 334.64→278.55 MB（反向下降 56）、私有 240.41→301.74 MB（**+61.33，超出「< 50 MB」判据**）、
      句柄 889→973 为有界锯齿（8 次 −83~−165 回落，峰值 887~973 无单调爬升）、线程 17→18。
      私有斜率前半段 0.4078 → 后半段 **0.1212**（衰减 3.4 倍），且同窗口工作集在下降，故判定
      **非泄漏、但私有水位高于判据**，如实记为「有条件通过」而非「通过」。两条未触及路径如实登记：
      `app.log` 峰值仅 42280 B（F-30 的 1 MB 轮转没跑到）、末轮 DB 仅 315 行（`TrimAsync` 淘汰没跑到），
      均由 T-20/T-24 单测覆盖
- [x] 修复 F-41：四处 `_ = RefreshAsync()` 式「发了不管」的刷新**没有任何 catch**。实测 .NET 10 会
      **吞掉**未观测的任务异常（3 个故障任务 → 2 次 `UnobservedTaskException` 事件、进程存活过所有 GC 轮），
      所以这不是崩溃风险；但实测 `QueryAsync` 在历史库被删时确实抛 `SQLite Error 1: 'no such table: clips'`，
      于是故障一旦发生，面板**静默停在旧列表**、日志一行不写。改为统一 `FireAndForget`/`ObserveAsync` 守卫：
      成功即复位、取消不算失败、一次故障期只报一次（日志 + Fluent 提示）；复位点放在 `RefreshAsync` 成功路径
      而非观察器里，因为固定/删除/清空这些直接 await 的调用点不经过观察器
- [x] 句柄爬升排查：浸泡中曾看到句柄单调上涨（约 +6/采样），用三项隔离探针逐个排除——
      `Process.GetProcessById`+`Dispose`（30 次，566→545）、`SqliteClipRepository` AddOrTouch+Trim
      （200 次，391→391）、`Clipboard.GetDataObject`（120 次，417→417）**全部为 0**；
      浸泡序列本身出现 −165 / −83 的回落，判定为 GC 有界锯齿而非泄漏

## 界面修复（2026-11-03）

- [x] 托盘菜单收紧到 196 DIP 宽、行高更紧凑；「打开剪贴板」与「设置/清空历史」之间、以及「退出」之前
      各加一条分隔线，视觉上分成三组
- [x] 搜索框去掉原生 `TextBox` 的蓝色焦点下划线，改成 Fluent 圆角容器 + 内嵌无边框输入框
- [x] 设置窗口的滚动条从浮动状态改回贴住内容区右边缘
- [x] 设置窗口底部按钮改为 `取消 / 保存 / 应用`，「应用」就地生效不关窗口，结果用行内提示代替弹窗
- [x] 浅色模式对比度：边框改用 `SurfaceStrokeColorDefaultBrush`、次要文字改用
      `TextFillColorSecondaryBrush`，不再发白
- [x] 清空的二次确认从 Win32 `MessageBox` 换成自绘 Fluent 对话框（`FluentMessageWindow`）：
      圆角 + Acrylic、强调色主按钮、`Esc`/`Enter` 快捷
- [x] 修掉 `FluentMessageWindow` 里 keyed `Style` 未 `BasedOn` 主题样式导致按钮退回原生灰底的问题

### 第二轮界面修复（2026-11-03，逐像素验收）

- [x] 筛选按钮行的左内边距从 6 改成 12，与顶部搜索框同一条 12/10 竖线；实测搜索框与「全部」的左边框
      都在 x=16px（420 DIP 面板 @125%）
- [x] 筛选按钮从胶囊改成圆角矩形描边：`Padding 12,5` / `CornerRadius 6` / `BorderThickness 1`，
      未选中用 `ControlStrokeColorDefaultBrush`，选中时填充与描边都用强调色。实测未选中按钮上下各有
      一条 lum=194.6 的 1px 描边、内部 238.5，不再是无边界色块
- [x] 每个条目之间加一条内缩到文字边缘的分割线（卡片 6 边距 + 8 内边距 = 14），用
      `DividerStrokeColorDefaultBrush`；行被选中时该线隐藏，否则会横切高亮块。实测右侧留白列上每 75px
      出现一条 band=190.6 / 邻行 238.5 的发丝线
- [x] 底部汇总、热键提示、清空/设置文字从 Tertiary 改到 Secondary；实测最深像素 (47,48,47)，
      对面板底色对比度 **11.55:1**
- [x] 修「点应用后面板变白」：`ThemeService.Apply` 现在在主题未变时直接返回，不再每次应用都调
      `ApplicationThemeManager.Apply`——那会连带 `WindowBackdrop.RemoveBackdrop` + `RemoveBackground`
      把面板的 Acrylic 抹成纯白；主题真的变了才走重建，并在重建后把每个 `FluentWindow` 的 backdrop
      重新贴回去。实测应用前后 4331 个背景采样点仅 2 点不同（且都是新剪进来的内容），背景色
      前后完全一致、Acrylic 渐变仍在
- [x] 删掉设置页底部的行内「已应用，设置立即生效。」提示：它会被窗口边缘挡住，且不属于用户需要的信息。
      校验失败的错误提示保留
- [x] 「应用」按钮改成脏状态指示：`AppSettings.Normalize()` + `ContentEquals()` 逐字段比对表单快照，
      任一控件改动就把按钮从 `Secondary`（白色描边）切到 `Primary`（强调色填充），应用成功后切回。
      实测初始白描边 → 改条数后变蓝填充 → 点应用后恢复白描边且无提示文字
- [x] 顺带修掉并发缺陷：`SqliteClipRepository` 原先用 `Cache = SqliteCacheMode.Shared`，共享缓存模式下
      SQLite 走**表级锁**，并发写会抛 `SQLITE_LOCKED`（`database table is locked: clips`），而
      `busy_timeout` 只重试 `SQLITE_BUSY`、对 `SQLITE_LOCKED` 无效。改为私有缓存 + 每连接
      `PRAGMA busy_timeout = 5000`，WAL 下读写并行。压力测试 `ConcurrentTrimAndQueryDoNotThrow`
      从 36s 且失败 → 10s 全绿

### 第三轮界面修复（2026-11-03，滚动条与左右边距）

- [x] 修「滑条盖住卡片边缘」：WPF-UI 的 ListBox 模板把滚动条做成**浮层**压在内容区上，拇指正好画在
      卡片右边缘。改为自绘 `HistoryScrollViewer` + `GutterScrollBar`：滚动条独占右侧 12 DIP 沟槽，
      与卡片列不重叠；拇指本体画 6 DIP、两侧各缩进 3 DIP（`Thumb` 模板根用透明 `Grid` 撑满 12 DIP，
      保证整条沟槽都能拖动）。实测 125% 下拇指 513..519px、卡片右缘 508px，**留 5px 间隙**；
      200% 下拇指 820..831px、卡片右缘 813px，留 7px
- [x] 修「内容卡片左右边距不同」：根因是继承来的 `GroupItem` 模板带**左侧内缩**，条目容器比列表左缘
      缩进 5 DIP、右缘却贴着列表右缘，于是卡片的左留白比右留白宽。用 `HistoryGroupStyle`（裸
      `ContentPresenter` + `ItemsPresenter`）覆盖掉主题模板，条目容器（UIA 实测 x=−1498 w=836）
      与列表（x=−1498 w=836）完全对齐
- [x] 卡片与分割线改为对称内缩：卡片 `Margin="12,2"`（两侧各 12 DIP），分割线 `Margin="20,0,20,0"`
      （12 边距 + 8 内边距，正好落在文字边缘），分组标题同步到 20。实测 125% 下卡片填充 16..508
      （左右各 15px）、分割线 26..498（左右各 25px）；200% 下卡片 26..813（各 26px）、
      分割线 42..797（左 40 / 右 41，1px 为取整误差）
- [x] 四个入口回归全部通过：`ScrollPattern` 可编程滚动 0%→40%→0%、鼠标滚轮、拇指拖动（0%→42.8%）、
      ↑/↓ 键 30 次（0%→29.5%→0%），虚拟化与分组不受影响

### 第四轮界面修复（2026-11-03，全应用文字对比度）

- [x] 修「文字都很浅」：`ThemeService.LightOverrides` 把 `TextFillColorSecondaryBrush` 设成
      `0x5C`（36% 黑），比同一张表里的 `TextFillColorTertiaryBrush`（`0x8A`，54%）**还浅**——
      层级是反的，而次级/三级文字恰好承担了筛选按钮、分组标题、副标题和底部汇总，实测只有
      **1.71:1**。重排为 主 90% / 次 82% / 三 75% 黑
- [x] 补齐全应用**深浅两套**文字阶梯：深色主题此前完全没有覆盖，留着 Fluent 默认的 77%/55% 白，
      11px 副标题同样发灰。现为 主 100% / 次 92% / 三 78% 白，并同步 `TextPlaceholderColorBrush`
      与 `TextFillColorDisabledBrush`
- [x] 数值高于 Fluent 默认是有依据的：ClearType 下小字号字干只覆盖约 70–75% 的标称 alpha，
      名义 62% 的比例在屏上只剩四成左右，这正是「发白」的观感来源
- [x] `ApplyContrastOverrides` 改为「先移除另一套、再写入本套」：否则运行时切主题会残留上一套画刷，
      继续遮蔽新主题字典
- [x] 实测（WCAG 对比度，面板与设置页各取采样）：
      浅色 筛选/分组标题/底部汇总 **1.71 → 7.82:1**、副标题 **5.19:1**、标题 11.92:1、
      底部「清空/设置」12.57:1、设置页说明文字 **3.87 → 5.36:1**；
      深色 次级 13.76:1、标题 15.13–16.21:1、设置页说明 12.52:1。两套主题均 ≥ 4.5:1

### 第五轮界面修复（2026-11-03，宽图被裁）

- [x] 修「图片太宽显示不全」：`ThumbnailSizeConverter` 的宽度上限取的是**列表宽度 × 92%**，
      而卡片实际可用宽度 = 列表宽度 − 12（外边距）×2 − 8（内边距）×2 = 列表 − 40 DIP。
      92% 比可用宽度还宽 8px，图片因此压进卡片右内边距并被裁掉右缘。改为绑定
      **条目内容区（卡片内 `ContentPresenter`）的 `ActualWidth`**，比例系数 0.92 → 1.0
- [x] 实测 125%：条目宽 523px，卡片内容宽 523−50 = 473px；最宽的几张图渲染为 473×132 / 473×159，
      右缘留白恰好 25px（= 12+8 DIP），无一越界（改前 481 宽 → 越界 8px）
- [x] 用源图逐像素校验未被裁切：把 `536×120` 的源 PNG 缩放到渲染尺寸后与屏幕截图比对，
      左/中/右各取 40px 条带，平均绝对误差 1.7 / 1.4 / **2.2**（纯缩放误差）——最右侧内容完整可见
- [x] 长宽比全程保持（1533×428 → 3.583、460×53 → 8.679、256×92 → 2.783，与源图一致），
      细窄图不放大（`StretchDirection=DownOnly` 语义不变）
- [x] 新增 `ThumbnailSizeConverterTests`（4 例：宽图压到内容宽、高图压到 75% 视口高、
      窄图保持原始像素尺寸、首帧 `ActualWidth=0` 回退自然尺寸），测试基线 333 → **337**

### 第六轮界面修复（2026-11-03，输入法组字时占位提示不消失）

- [x] 修「输入文字时占位提示不消失、和文字重叠」：占位 `TextBlock` 的可见性绑在
      `SearchText` 上，而 **输入法组字期间 `TextBox.Text` 仍是空的**——组字串由 IME 自己画在
      输入框上，`TextChanged`/绑定都不触发，于是「搜索剪贴板」一直留在字底下。截图复现：
      组字显示 `撒` 时提示仍在，候选条为「1 撒 2 萨 3 洒 4 仨 5 卅」
- [x] 可见性改由代码驱动：`PreviewKeyDown` 收到 `Key.ImeProcessed`（组字首键）立即隐藏，
      `TextChanged` 负责常规输入/粘贴/清空，`LostKeyboardFocus` 收拾「组字被取消」留下的隐藏态。
      实测四态：空 → 显示提示；组字 `sa` → 提示消失；提交为 `撒` → 提示消失；清空 → 提示恢复
- [x] 顺带删掉因此失去唯一使用者的 `EmptyStringToVisibilityConverter` 及其资源项

### 第七轮界面修复（2026-11-03，图片预览模式可切换）

- [x] 新增设置项 **图片预览**：`原始尺寸` / `缩略图`，放在设置页「外观」里（带一行说明）。
      原始尺寸沿用按卡片内容宽等比缩小（一图像像素对一设备像素）；缩略图固定 160×120 DIP 小框，
      每行高度立刻收敛
- [x] `AppSettings.ImagePreview` 进模型（默认 `Original`）、`Clone`、`Normalize`（未知值回落
      `Original`）与 `ContentEquals`；设置页的「应用」脏状态会把它算进去
- [x] 面板通过 `MainWindow.ImagePreviewMode` 依赖属性接收模式，缩略图尺寸的 `MultiBinding`
      多一路绑定；`App.SaveSettings` 应用后写回该属性，**无需重启**即重排
- [x] 转换器新增缩略图分支：宽高都夹在 160×120 内，仍不放大、仍受卡片内容宽约束（窄面板不会溢出）；
      新增 3 例测试（宽图压到 160、窄图不放大、窄内容区仍不溢出）
- [x] 实测：原始尺寸下图片宽 473px（= 卡片内容宽），切到缩略图后同一批图变 200px
      （160 DIP × 125%），再切回 473px，全程 `settings.json` 同步写入 `Thumbnail` / `Original`
- [x] 顺带修掉设置窗口的隐藏泄漏：面板唤出时 `ShowPanel()` 只是 `Hide()` 设置窗口，再次打开会
      新建一个、旧的留在内存里（实测一次会话累积 5 个隐藏窗口）。改为新建前先 `Close()` 旧的，
      复测只剩 1 个

### 第八轮界面修复（2026-11-03，图片大 / 中 / 小三档）

- [x] 图片预览改为 **大 / 中 / 小**：大沿用卡片内容宽度内等比显示；中保留 160×120 DIP 内的完整预览；
      小在左侧 40×40 DIP 圆角图标位居中裁切填充，旁边常驻显示尺寸、来源和时间，恢复紧凑列表布局
- [x] 大/中移除常驻信息行，信息改为图片自身的 `ToolTip`，停留 350 ms 后提示；卡片空白区域不挂图片信息。
      提示不参与条目测量、不覆盖短图；固定/删除独立放在右侧，`Canvas` 不撑大图片布局
- [x] 小档使用圆角 `ImageBrush` 背景，避免 `UniformToFill` 的 Image 渲染尺寸超出方形槽位；普通文本/文件条目
      保持图标加文字，并用受宽度约束的 Grid 使长标题正常省略、不挤压右侧操作
- [x] 设置保存即时更新预览尺寸和条目布局，现有行及后续捕获的新行都沿用当前模式；「应用」脏状态和 JSON
      持久化纳入三档。启动时标准化旧值：`Original` → `Large`、`Thumbnail` → `Medium`，未知值回落 `Large`
- [x] 隔离 WPF 集成验收：深浅主题下的宽图、扁图、窄图、小档方形槽位、普通文本/文件布局，提示开关前后行尺寸
      不变；真实设置页的应用处理链、脏状态复位、旧值持久化，以及三档固定/取消固定/删除命令均通过，不删除真实历史
- [x] 自动测试 **366 例通过**（Core 176 + App 190），覆盖旧模式迁移、三档布局、属性通知、图片路径增减、
      新旧行的模式传播，以及三档尺寸与不放大约束
- [x] `build.cmd` 全部通过，重新发布自包含与框架依赖两个单文件产物；两个版本均完成启动烟测，当前运行自包含版

### 第九轮界面修复（2026-11-04，扩展动作改右键菜单）

- [x] 移除底部扩展动作栏（与左键粘贴冲突、需要先选中），扩展动作统一走**条目右键菜单**；
      列表底部留白归页脚
- [x] 菜单复用托盘的 `TrayMenuWindow`（Acrylic Fluent 弹窗）：按记录过滤动作、带图标、
      无适用动作时显示禁用占位行；支持 Esc、点外部关闭、菜单键 / Shift+F10（锚定选中行右缘）
- [x] 执行绑定右键时的记录快照（`ExtensionActionRequest`），选项弹窗、防重复执行、错误提示不变；
      `MainViewModel` 新增按条目的纯匹配方法，底部栏的选中态耦合全部移除
- [x] 顺带修掉 `TrayMenuWindow` 的潜在崩溃：窗口关闭过程中失焦再次 `Close()` 会抛
      "cannot call Close while closing"（自动化探针实测触发），加 `_closing` 标记守卫
- [x] 隔离 WPF 探针 21 项断言通过：打开/取消/点击路径均不触发粘贴、面板不失焦隐藏、
      无动作占位禁用、命令确实作用于右键的记录；单元测试 **368 例全绿**（Core 176 + App 192）

### 第十轮界面修复（2026-11-04，全维度 UX 审查修复）

- [x] 依据 `docs/COMPREHENSIVE-REVIEW-PLAN.md` 完成三维度审查（静态接线/视觉测量/交互走查），
      发现清单见 `docs/UX-REVIEW-FINDINGS.md`（3×S2 + 13×S3 + 5×S4）
- [x] 修复 3 项 S2：确认/通知弹窗不再隐藏面板（作用域抑制 + 关闭后归还焦点）；设置窗 Esc=取消、
      脏表单关闭前确认；选项弹窗 Esc 可取消
- [x] 修复交互项：组头点击不再拖走面板（白名单补 GroupItem）；菜单关闭延迟 150→80ms；
      粘贴失败改非模态 Toast；删除 SearchBox 死分支
- [x] 修复可访问性：面板 32/32 按钮补 UIA 名称，筛选行名绑定 Label（实测全名覆盖）
- [x] 修复视觉/一致性：页脚 12 竖线、菜单/对话框圆角统一 6、任意时刻仅一个 accent 按钮、
      跨年日期带年份、三组术语统一（历史/参数/扩展包）、英文残句本地化、托盘分隔符统一
- [x] 死代码清理：两个未绑定命令与 closeRequested 管线、扩展动作死成员、孤儿转换器、7 个幽灵命名
- [x] 新增跨年日期测试；**386 例全绿**（App 204 + Core 182）；编码检查 OK；双版本发布并实测验收

### 第三轮差分审查（2026-11-05，audit-only）

- [x] 按 `project-review-playbook` 执行 v1.0.0→v1.1.0 差分审查（33 文件、+1878/−119），
      产出 `docs/REVIEW-1.1.0-PLAN.md` / `-FINDINGS.md` / `-REPORT.md`
- [x] 发现：S1×1（筛选行溢出，时间芯片/来源下拉不可达，三档宽度+滚轮实测）、
      S2×2（另存为藏面板、位置恢复 DIP/物理混用）、S3×3（来源选择静默重置、非法数据目录诊断差、跨午夜不刷新）
- [x] 未改代码；建议 v1.1.1 热修（FIX-01..05 已列方向与 RED 测试）
- [x] 过程教训回填 skill 反模式第 13 条：「能查到控件」≠「用户能看到/点到」

### v1.1.0（2026-11-05，连续实施完成）

- [x] 阶段一：托盘暂停捕获（会话级）、粘贴后保持面板、右键「粘贴为纯文本」、图片另存为
- [x] 阶段二：SQLite schema 迁移机制（PRAGMA user_version + 事务迁移）、settings 版本字段与
      Theme 规范化、数据目录可重定向（--data-dir / portable.marker，实例名派生）
- [x] 阶段三：面板可调尺寸+跨会话记忆（工作区夹取）、删除撤销（trash + 6 秒 Toast）、
      搜索命中高亮、今天/7天与来源应用过滤
- [x] 阶段四：动作执行注册表（Register 即扩展）、脚本 stdin JSON（text/html/filePaths/sourceApp/options）
- [x] 实测：440→ 测试 **420 例全绿**（App 232 + Core 188）；真机验收筛选芯片/来源下拉、
      菜单内置项（文本/图片分别正确）、尺寸 800×1000px 调整后跨重启保持
- [x] 关键修复：PowerShell 5.1 Console.In 保留 BOM 导致 JSON 解析失败 → 无 BOM 负载（非 ASCII 转义）
- [x] 发布 v1.1.0（GitHub Release 双资产）

### GitHub 发布 v1.0.0（2026-11-04）

- [x] 仓库：https://github.com/xiaopeng66/ClipBar （public，MIT License，main 分支）
- [x] Release：https://github.com/xiaopeng66/ClipBar/releases/tag/v1.0.0
      资产 ClipBar.exe 70.4 MB（自包含，含 P-0 修复）+ ClipBar-lite.exe 9.2 MB（框架依赖）
- [x] 隐私扫描（无人名/无路径泄露）后 git init 提交 105 文件；.gitignore 排除 bin/obj/dist/数据文件
- [x] 后续计划：`docs/ROADMAP.md`（v1.1 快赢包 → v1.2 体验项 → v1.3 扩展性地基 → backlog）

### 第十一轮：产品更名 Clipboard11 → ClipBar（2026-11-04）

- [x] 命名：Clipboard + Bar，与 PulseBar/TrafficMonitor 托盘工具命名一致；联网核查无同名在役产品
- [x] 方案与记录：`docs/RENAME-PLAN.md`（13 项变更点全量盘点 + 数据迁移设计 + 回退策略）
- [x] 实施：97 文件全局替换（命名空间/项目/清单/构建/文档），项目目录与解决方案改名，
      程序集/exe 更名 `ClipBar.exe`，dist 旧名产物清除
- [x] 数据目录迁移：`%LOCALAPPDATA%\Clipboard11` → `ClipBar`（首启 Move，失败回退复制），
      实测真实历史完整（db/settings/images 67 文件/extensions 全部随迁）
- [x] SnipDo 动作重装为 `0ClipBar`（启动脚本指向新 exe，删除旧目录）
- [x] 自启动自愈加固：Run 值改名孤儿清理 + 「设置开但注册表失效 → 重写」而非静默关闭
- [x] 项目根目录改名完成：`projects/clipboard11` → `projects/clipbar`（用户关闭 ZCode 后手动执行，
      文档内绝对路径引用已同步修正）
- [x] **386 例测试全绿** + 编码检查 + 双版本发布 + 实测验收（窗口名/历史条数/数据目录）

## 体验修复（2026-11-02）

- [x] 面板可拖动：`RootPanel` 上的 `PreviewMouseLeftButtonDown` + `IsInteractive` 白名单（按钮/输入框/
  滚动条/列表项不触发拖动），拖动后本次运行内不再自动居中
- [x] 面板定位改为鼠标所在显示器工作区居中（`MonitorFromPoint` + `GetMonitorInfo` + `GetDpiForMonitor`
  换算物理像素→DIP），修掉多显示器下跑到副屏的问题
- [x] 文本清晰度：`TextFormattingMode="Ideal"` + `TextRenderingMode="ClearType"`，125% 缩放不再发灰发块
- [x] 图片缩略图按原始像素尺寸显示：`ThumbnailSizeConverter` 用「图片像素 ÷ 窗口 DPI」定尺寸，
  再由列表实际宽高限制（宽 92%、高 75%，最小 96 DIP）等比缩小；圆角用 `RectangleGeometry` 裁剪，
  图片首次渲染时才解码（`ClipItemViewModel.Thumbnail` 惰性加载）
- [x] 托盘菜单换成自绘 Fluent Acrylic 圆角弹窗（按图标所在显示器 DPI 定位），
  替代原来模糊的 WPF `ContextMenu`

## 后续可做（非必须）

- [ ] 选中文本后的“长按左键浮出”交互（需要低级鼠标钩子 + 注入，风险高）
- [ ] 扩展执行结果显示在面板内（目前 PowerShell 结果走 `after` 的弹窗/剪贴板）
- [ ] 条目分组折叠、按来源应用过滤
- [ ] 备份/导出历史

## 边界

不做云同步、OCR、AI、手机端、接管或屏蔽系统 Win+V。

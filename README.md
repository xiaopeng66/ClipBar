# ClipBar

仿 Windows 11 **Win+V** 的剪贴板历史工具，并且可以**作为 SnipDo 的剪贴板插件**（替换 SnipDo 自带的 10 条剪贴板）。

单文件 exe、纯本地存储、无云同步、无账号。

## 快速开始

1. 双击 `dist\ClipBar.exe`（首次运行会自建 `%LOCALAPPDATA%\ClipBar\`）。
2. 按 **`Win+Alt+V`** 唤出面板（默认热键；你最初选的 `Win+Shift+V` 被别的软件占用，已改为此组合）。
3. 复制任意文本/图片/文件，内容自动进入历史。点条目即粘贴回原窗口。

## 功能

### 剪贴板历史

- 捕获 **文本**、**富文本 HTML**、**图片**、**文件列表**。
- SHA-256 内容去重：相同内容只留一条并置顶。
- 最多保留 500 条非固定项（可改），**固定项不参与淘汰**。
- 记录来源应用与时间，列表按“今天 / 昨天 / 更早”分组。
- 搜索（文本 / 文件名 / 来源应用，命中片段高亮）、类型筛选（全部 / 文本 / 图片 / 文件）与来源应用筛选；
  面板变窄时筛选项自动换行，不会被裁掉。
- 右键条目：内置「粘贴为纯文本」（文本条目）或「图片另存为…」（图片条目），
  其后是导入的 SnipDo 扩展动作；左键点击仍是粘贴。
- 面板可自由调整大小并记住位置（最小 360×480）；删除后面板内出现 6 秒「撤销」提示条，不需要弹窗。
- 固定 / 删除按钮统一在每一行的右侧（图片行也一样，不再浮在图片上）。
- 托盘菜单可「暂停捕获 / 恢复捕获」；设置里可开启「粘贴后保持面板打开」连续粘贴。
- 键盘：`↑` `↓` 选择，`Enter` 粘贴，`Delete` 删除，`Ctrl+P` 固定，`Ctrl+F` 聚焦搜索，`Esc` 关闭。
- 默认排除密码管理器（1Password / Bitwarden / KeePass / KeePassXC / Keeper），列表可编辑。

### 作为 SnipDo 的剪贴板插件

SnipDo 的剪贴板栏是一个 **PasteAction**（`BuildInPasteActions\0PasteMain\data.json`），不是扩展。
所以 ClipBar 反向提供自己的 paste action，把那一格换成「剪贴板」→ 打开本应用的历史面板。

设置 → **把 ClipBar 装进 SnipDo**：

| 按钮 | 作用 |
| --- | --- |
| **安装到 SnipDo** | 把 paste action 写进 SnipDo 的用户目录 `PasteActions\0ClipBar\`，重启 SnipDo 后生效 |
| **导出 .pbar** | 只生成插件包到 `%LOCALAPPDATA%\ClipBar\snipdo\ClipBar.pbar`，不写 SnipDo 目录 |
| **打开插件目录** | 打开上面那个目录 |

目录名 `0ClipBar` 是刻意的：SnipDo 按**文件夹名排序**加载 paste action，`0ClipBar` 排在自带的
`0PasteMain` 前面，所以栏里第一个就是本应用。

生成的脚本 `main.ps1` 是纯 ASCII（Windows PowerShell 5.1 会把无 BOM 的 UTF-8 当 ANSI 读）。
它先尝试用命名事件 `Local\ClipBar.Show` 唤醒已经在运行的实例；没有实例时用 `--show` 启动本应用。

同一套动作也可以脱离界面跑（便于脚本化/重装后修复）：

```cmd
ClipBar.exe --export-snipdo     :: 只重建插件包
ClipBar.exe --install-snipdo    :: 重建并写进 SnipDo 的 PasteActions
```

> 改了 exe 路径（比如换了安装位置）或改了快捷键后，重新点一次「安装到 SnipDo」，脚本里的路径才会更新。

### 导入的 SnipDo 扩展（可选）

反过来也支持：设置 → **导入的 SnipDo 扩展** → **导入 .pbar**，把 SnipDo 的插件包装进 ClipBar。
如果你的扩展是**已经装进 SnipDo 的**（SnipDo 把它存成解包目录，清单文件叫 `data.json`），用 **导入目录** 直接选那个文件夹即可：

```
%LOCALAPPDATA%\Packages\JohannesTscholl.Pantherbar_3hp4skfxf5x2g\LocalCache\Local\SnipDo\Data\<数字ID>\
```

导入后在**剪贴板条目上点右键**即可使用这些动作：菜单只显示对这一条记录适用的动作（按 SnipDo 的
requirements/regex/来源应用规则过滤），左键点击条目仍然是粘贴，两者互不干扰。

支持的动作类型：

| 类型 | 说明 |
| --- | --- |
| `url` | 用占位符拼出 URL 并在浏览器打开 |
| `powershellFile` | 运行插件自带的 `.ps1`，stdout 作为结果 |
| `pythonFile` | 运行插件自带的 Python 脚本 |
| `keyCombo` | 发送按键组合（如 `Ctrl+Shift+T`） |

支持的占位符（长、短两种写法都认，真实插件包两种都在用）：

- `{PANTHERBAR_PLAIN_TEXT}` / `{PLAIN_TEXT}` —— 原始文本
- `{PANTHERBAR_URLENCODED_TEXT}` / `{URLENCODED_TEXT}` —— URL 编码（空格 → `+`）
- `{PANTHERBAR_URLENCODED_ALT_TEXT}` / `{URLENCODED_ALT_TEXT}` —— URL 编码（空格 → `%20`）
- `{PANTHERBAR_HTMLENCODED_TEXT}` / `{HTMLENCODED_TEXT}` —— HTML 实体编码
- `{APPOPTION <id>}` —— 弹出选择框让用户挑一个 `options` 值

清单里的 `icon_colored`、`monochrome` 字段也会读取：优先用彩色图标，缺失时退回 `icon`，再缺失则用内置闪电图标。

图标支持 `.svg`（真实 SnipDo 包基本都是 svg，24×24）：内置了一个小型矢量渲染器，认得 path / rect / circle / ellipse / line / polyline / polygon、嵌套 `<g>`、`matrix/translate/scale/rotate` 变换、纯色与线性/径向渐变、描边；纯黑单色图标会自动换成中性灰，避免在深色主题下看不见。超出这个子集的图形会被忽略，完全渲染不出来时回退到内置闪电图标。

支持的过滤与行为：

- `requirements`：`HAS_URL` / `IS_URL` / `EMAIL` / `FILEPATH`
- `regex`：只对匹配的文本出现
- `showOnlyApps` / `noShowApps`：按前台进程名显示/隐藏
- `after`：`PASTE_RESULT` / `SHOW_RESULT` / `SHOW_SUCCESS` / `COPY_RESULT`

PowerShell 脚本会收到 `-PLAIN_TEXT`、`-URLENCODED_TEXT`、`-HTMLENCODED_TEXT`、`-URLS`、`-APPOPTION_*` 参数，
工作目录为插件解压目录，所以脚本旁边放资源文件可直接用相对路径。脚本写 stderr 或非零退出码会被当成失败并弹窗提示。

**兼容性边界**（v1 未实现）：

- 插件图标若是 SVG 里的复杂特性（`<use>`、`<clipPath>`、`<mask>`、滤镜、嵌入位图、CSS 类选择器），这些部分不会绘制，可能只剩部分图形。
- 不执行插件里的 `.exe` 动作（`file` / `executable` 类型），只跑 `.ps1` / `.py` / `url` / `keyCombo`。
- 不支持 SnipDo 的“选中文本后浮出工具条”交互（那是 SnipDo 自身的壳，我们只做面板内的动作条）。

## 设置

- **快捷键**：默认 `Win+Alt+V`，可改成 `Ctrl+Alt+V` 等；组合被占用时保存会提示。
  Win+V 被 Windows 系统独占，普通程序无法注册。
- **历史条数**：10–5000。
- **主题**：跟随系统 / 浅色 / 深色，切换即时生效。
- **图片预览**：**大 / 中 / 小**三档，应用后立即生效，无需重启。
  - **大**：按卡片可用宽高等比缩小，完整显示图片，不放大小图。
  - **中**：在 160×120 DIP 内等比显示完整图片，不放大小图。
  - **小**：缩略图放入左侧 40×40 DIP 图标位，旁边常驻显示图片尺寸、来源和时间。
  - 大/中不常驻显示信息，指针停在图片上时通过悬浮提示查看；固定/删除仍可在条目右侧操作。
  - 旧配置的「原始尺寸 / 缩略图」会自动对应到「大 / 中」。
- **行为**：点击即粘贴、粘贴后保持面板、开机自启、是否记录图片/文件/HTML。
- **排除的应用**：每行一个进程名。
- **数据目录**：`%LOCALAPPDATA%\ClipBar\`（`history.db`、`images\`、`settings.json`、`extensions\`、`app.log`）。

托盘菜单：打开剪贴板 / 设置 / 清空历史 / 退出（分隔线把它们分成三组）。单实例运行，重复启动只会唤出已有窗口。
托盘右键菜单是独立的 Acrylic 圆角弹窗（不是 WPF 原生 `ContextMenu`），会按托盘图标所在显示器的 DPI 缩放，
所以 125%/150% 缩放下文字和边框都是清晰的。

## 面板操作

- **拖动**：按住面板空白处（标题区、列表间隙、底部栏空白）拖动即可移动窗口；搜索框、筛选按钮、
  条目卡片上的图标按钮仍然响应各自的点击，不会误触发拖动。手动拖动过之后，本次运行内不再自动居中。
- **自动位置**：首次唤出时在鼠标所在显示器的工作区居中（多显示器下不会跑到副屏去）。
- **图片显示**：大/中档保持完整长宽比；大档宽度不超过卡片内容区，高度不超过列表视口的 75%，
  中档限制在 160×120 DIP 内，两档都只缩小不放大。小档在 40×40 DIP 圆角图标位中居中裁切填充。
  大/中通过 `RectangleGeometry` 裁剪圆角，小档用圆角背景画刷；悬浮信息不参与卡片布局，
  不会撑大窄图或遮住扁图。图片首次渲染时才解码，宽度超过 1600px 的源图会先降到 1600px 再显示。
- **文字渲染**：面板、托盘菜单、设置窗口统一用 `TextFormattingMode="Ideal"` + `TextRenderingMode="ClearType"`，
  在非整数缩放（如 125%）下不会出现发灰、块状的灰度抗锯齿字形。

## 下载

从 [GitHub Releases](https://github.com/xiaopeng66/ClipBar/releases/latest) 下载：

| 文件 | 说明 |
| --- | --- |
| `ClipBar.exe`（约 70 MB） | 自包含单文件，双击即用，无需安装 |
| `ClipBar-lite.exe`（约 9 MB） | 框架依赖版，需要 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |

## 构建

```cmd
build.cmd
```

它依次跑 `dotnet test`，然后发布两个单文件产物：

| 产物 | 体积 | 说明 |
| --- | --- | --- |
| `dist\ClipBar.exe` | **约 70 MB** | 自包含 .NET 10 运行时，压缩后单文件，双击即用，无需另装任何东西 |
| `dist-lite\ClipBar.exe` | **约 9 MB** | 框架依赖，需要机器上装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |

默认用 `dist\ClipBar.exe`。机器上已经有 .NET 10 桌面运行时时可以换 `dist-lite`，体积小 8 倍。

> 体积构成：自包含版必须打包整个 `Microsoft.WindowsDesktop.App` 框架（WPF + 共用的 WinForms 程序集），
> 未压缩是 165 MB。`EnableCompressionInSingleFile` 用 deflate 压到 70 MB，启动实测反而更快
> （1.06 s vs 1.23 s，少读 95 MB）。再往下减只能走框架依赖那条路。

只跑测试：

```cmd
dotnet test
```

测试分两套：`ClipBar.Core.Tests`（189 例，模型/仓库/搜索/清单解析/安全路径/设置比较与旧预览模式迁移/schema 迁移）和
`ClipBar.App.Tests`（230 例，热键/面板定位/剪贴板监听/单实例/SVG/扩展执行器/视图模型压力/粘贴结果与目标守卫/刷新失败上报/损坏图片兜底/图片尺寸与三档布局切换/右键扩展动作/跨年日期），
共 **419 例**。

## 安全与稳定性

本项目的威胁模型把**所有外部输入当作不可信**：剪贴板内容、导入的 `.pbar`/`.zip`/`data.json`、
扩展里的 `.svg` 图标、`settings.json`、清单中的路径字段。

- **扩展包**：解包前校验条目路径（拒绝 zip-slip 与绝对路径），限制条目数和解压后总大小，
  防止一个恶意包撑爆磁盘或写到数据目录之外。
- **扩展执行**：脚本类动作做包含检查、URL 动作限制协议白名单；连点有重入锁，不会并发跑同一个脚本。
- **剪贴板输入**：文本上限 1 MB（超出截断而非拒绝），文件列表上限 200 条，避免一行撑成几兆。
- **持久化**：`settings.json` 原子写入，读失败时保留上一次可用配置并提示；`app.log` 超过 1 MB 轮转，
  只留 `app.log.1`。
- **退出**：剪贴板监听线程用显式停止标志退出，不会在窗口关闭后再排入一次 `Show()`。
- **粘贴**：写剪贴板失败时（图片源文件已被清理、文件列表为空、剪贴板被别的程序长期占用）**不会**
  再发 Ctrl+V——否则粘进去的是剪贴板上一次的内容，常常正是刚复制的密码。这种情况下会明确提示「复制失败」。
- **后台刷新**：面板在每次唤出和每次捕获后都会刷新列表，这个刷新失败时会被**上报**（日志 + 一次提示），
  不会让列表静默停在旧内容。实测 .NET 10 会吞掉未观测的任务异常，所以「发了不管」的写法不会崩、
  但会静默失效，现已统一走守卫。
- **损坏图片**：历史里的图片文件被外部损坏/截断/替换成文本时，粘贴路径不会因为解码异常而「按了没反应」，
  而是明确返回「复制失败」；`settings.json` 的路径字段被改成畸形串（如 `::::`）同理。
- **句柄/内存**：2 小时连续复制浸泡（240 轮，含图片）实测句柄为有界锯齿（8 次 −83~−165 回落，峰值无单调
  爬升）、工作集**反向下降** 56 MB；私有字节偏高约 61 MB，但后半段斜率从 0.41 衰减到 0.12 MB/轮，
  判定为非泄漏。完整数据见方案文档 §7.2。

完整的审查记录（40 个缺陷 + 1 条加固、52 个用例及执行结果）见
[`docs/REVIEW-AND-STRESS-PLAN.md`](docs/REVIEW-AND-STRESS-PLAN.md)。

## 已知限制

- **管理员权限窗口**：Windows 的 UIPI 限制让普通权限进程无法读写提权窗口的剪贴板，这类窗口里复制/粘贴不会被记录/生效。把 ClipBar 也以管理员运行即可。
- **热键冲突**：`Win+Alt+V` 若与其它软件冲突，注册会失败并回退，请在设置里换一个组合。
- **图片体积**：超长边会压缩，避免历史库膨胀。
- **不做**：云同步、OCR、AI 摘要、手机端、接管/屏蔽系统 Win+V。

## 目录结构

```
projects/clipbar/
  ClipBar.slnx
  build.cmd
  src/
    ClipBar.Core/      # 模型、SQLite 仓库、SnipDo 清单解析与匹配（无 UI 依赖）
    ClipBar.App/       # WPF-UI 界面、剪贴板监听、热键、托盘、扩展执行器
  tests/
    ClipBar.Core.Tests # xunit
  dist/ClipBar.exe     # 单文件发布产物（自包含 + 压缩，约 70 MB）
  dist-lite/ClipBar.exe # 框架依赖单文件（约 9 MB，需 .NET 10 桌面运行时）
```

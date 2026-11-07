# ClipBar 原生浮动剪贴板交互改进方案

> 目标：在不依赖 SnipDo 的情况下，复刻 SnipDo 原生的「左键手势 → 浮动操作条 → 悬停展开剪贴板历史 → 点击即粘贴」体验。
>
> 本方案基于本机 SnipDo **3.0.88.0** 的安装二进制、反编译 C#、BAML/XAML 资源和实际 `PasteActions/0ClipBar/data.json` 复核整理。
>
> **实施状态（2026-11-07）**：Phase 0–4 的首个闭环已实现并通过端到端实测 — Raw Input 长按
> 600 ms → 浮动操作条 → 悬停 200 ms → 紧凑列表 → 点击行粘贴到原窗口。默认关闭，需在设置里
> 勾选「启用原生浮动剪贴板」。拖选文本（UIA TextPattern + Ctrl+C 回退）已接入同一状态机。

## 0. 已实现部分（与下文方案的对应关系）

| 方案项 | 落地文件 | 实测 |
| --- | --- | --- |
| 设置（开关、600ms、15px、200ms、1000ms、拖选开关） | `ClipBar.Core/Models/AppSettings.cs` + `SettingsWindow` | 491 单测通过 |
| Raw Input 手势状态机（纯逻辑，可测） | `Services/FloatingGestureStateMachine.cs` | 9 个单测 |
| Raw Input 服务（RIDEV_INPUTSINK，WM_INPUT 解码） | `Platform/RawInputInterop.cs`、`Services/RawMouseInputService.cs` | 真机事件到达 |
| 选区探测（UIA TextPattern + Ctrl+C 回退 + 超时） | `Services/SelectionProbe.cs` | 编译/守卫通过；UIA 路径未在真实应用逐项验证 |
| 浮动操作条（不激活、悬停 200ms 展开） | `FloatingActionBarWindow.xaml(.cs)` | 截图 + 日志 |
| 紧凑列表复用（新增 `ShowBelowAsync`） | `CompactPanelWindow` | 点击行粘贴成功 |
| 生命周期协调（外部点击、离开自动关闭、目标守卫、提权/全屏跳过） | `Services/FloatingBarCoordinator.cs`、`Platform/Elevation.cs` | 外部点击/自动关闭实测 |

实测证据：长按后出现 54×50 的浮动按钮窗；悬停后出现 375×559 的列表；点击
`AUDIT_FIXED_TOKEN_1_20261106` 行后目标输入框变为 `TARGET:AUDIT_FIXED_TOKEN_1_20261106`。

已知限制（下一阶段）：

- 浮动条本身因 `WS_EX_NOACTIVATE` 不接收点击（悬停已能展开，点击按钮暂不生效）；
- 拖选路径依赖 UIA，未在 Edge/Office 等真实应用逐项验证；
- 未做多显示器混合 DPI、200% 下浮动条尺寸/位置的系统验证；
- 未做 500 次连续手势与 30 分钟驻留压力。

## 1. 已确认的 SnipDo 行为基线

### 1.1 主操作条与剪贴板列表是两个窗口

SnipDo 的主操作条是 `SnipDo.ActionBar`，剪贴板历史是独立的 `SnipDo.Views.ActionBarClipboardHistory`：

- 主条：无激活、无焦点、置顶、无标题栏、透明背景；
- 主条按钮使用 WPF `MouseEnter`；
- 鼠标进入按钮后等待 **200 ms**，确认鼠标仍在同一个按钮；
- `paste_main` 是 SnipDo 内置特殊动作；命中后调用 `ShowClipboardBar`；
- 历史列表：宽约 **340 DIP**，最大高度约 **400 DIP**，独立无标题栏窗口，显示在主条下方；
- 点击历史行：把完整文本写回目标窗口并关闭历史列表；
- 主条和历史列表都不再被鼠标悬停时，约 1 秒后自动关闭。

对应 SnipDo 证据：

- `project/SnipDo/ActionBar.xaml`
- `project/SnipDo/ActionBar.xaml.cs:566-600`
- `project/SnipDo/Views/ActionBarClipboardHistory.cs:95-151,193-266`
- `actionbarclipboardhistory.xaml`

### 1.2 左键长按和拖选使用 Raw Input

SnipDo 当前实际使用 `RawMouseKeyFetcher`：

- `RegisterRawInputDevices(Mouse, InputSink, hiddenWindow)`；
- 隐藏窗口收到 `WM_INPUT`；
- 左键按下记录位置和时间；
- 默认长按 **600 ms**；
- 按住期间无移动才触发；
- 拖动超过横向或纵向 **15 px** 才视为拖选；
- 鼠标释放时，拖选持续超过约 **200 ms** 且仍检测到 I-beam 才获取选区；
- 提权前台窗口会被跳过，避免 UIPI 失败后弹出无效按钮。

对应 SnipDo 证据：

- `project/SnipDo/RawMouseKeyFetcher.cs`
- `project/SnipDo/AppController.cs:861-937`
- `project/SnipDo/SettingsStates.cs:54-58,1627-1636,1881-1885`

### 1.3 选区读取顺序

SnipDo 的 `TextFetcher.GetTextFromAutomationElement()`：

1. 获取当前焦点 UIA 元素；
2. 尝试 `TextPattern` ID `10014`；
3. 失败时尝试 TextPattern2 ID `10024`；
4. 调用 `GetSelection()` 与 `GetText(-1)`；
5. 读取不到时回退到：备份剪贴板 → 模拟 Ctrl+C → 最多等待约 800 ms → 读取文本 → 按配置恢复剪贴板。

UIA 读取绝不能放进 Raw Input 回调；必须异步、超时、失败即静默放弃。

对应 SnipDo 证据：

- `project/SnipDo/TextFetcher.cs:329-361`
- `project/SnipDo/TextFetcher.cs:364-409`

### 1.4 重要边界

本机实测 SnipDo 工具条是自绘窗口，不暴露可用的 UIA Button 节点。因此：

- **不能**从外部插件挂接 SnipDo 自己的 `MouseEnter`；
- SnipDo 的 `0ClipBar/data.json` 只是普通 paste action，不会自动获得 `paste_main` 特殊行为；
- ClipBar 必须拥有自己的浮动操作条和自己的悬停事件；
- SnipDo 的“悬停展开”不可由插件清单声明出来，只能由 ClipBar 自己复刻。

## 2. ClipBar 当前可复用基础

当前代码已经具备以下能力，不应重复造轮子：

- `CompactPanelWindow`：已有紧凑历史列表，点击行可粘贴；
- `PasteService`：已有文本/图片/文件粘贴、目标窗口守卫、剪贴板失败保护；
- `OutsideClickHook`：已有全局鼠标钩子；需新增 Raw Input 服务，不把外部点击钩子硬改成手势状态机；
- `FlashFreeShow`、`FrameBorder`、主题表面资源：可复用到新的浮动操作条；
- `AppPaths`、单实例事件、隔离 `--data-dir`：可复用测试和发布流程；
- 现有 UI 回归和 478 项测试门禁：新阶段必须保持全绿。

## 3. 目标交互状态机

```text
Idle
  ├─ 左键按下 → Pressed（记录目标窗口、屏幕点、时间）
  ├─ 移动超过 15 px → Dragging
  └─ 保持不动达到 600 ms → HoldTriggered

Dragging
  └─ 左键释放 → SelectionProbe
       ├─ 有非空选区 → FloatingBar
       └─ 无法读取/空选区 → Idle（不打扰用户）

HoldTriggered
  └─ 前台非提权、非全屏且目标有效 → FloatingBar

FloatingBar
  ├─ 鼠标进入 Clipboard 按钮并保持 200 ms → CompactPanel
  ├─ 点击 Clipboard 按钮 → 展开/收起 CompactPanel
  ├─ 点击外部 / Esc / 失去目标 → Close
  └─ 超时且鼠标不在浮动条 → Close

CompactPanel
  ├─ 点击历史行 → PasteService → Close
  ├─ 鼠标移到浮动条或列表 → 继续保持
  ├─ 点击外部 / Esc → Close
  └─ 目标窗口失效 → Close 或仅复制并提示
```

第一阶段与 SnipDo 对齐：**长按先显示浮动操作条，悬停 Clipboard 按钮再展开列表**。不要把长按直接绑定到完整主面板。

## 4. 分阶段实施计划

### Phase 0：基线与开关（先做）

目标：功能可关闭、可回滚、不会影响现有 ClipBar。

新增设置建议：

- `NativeFloatingEnabled`：默认关闭或按产品决定；
- `InvokePasteOnHold`：默认开启；
- `ClickHoldDuration`：默认 600 ms；范围建议 300–1200 ms；
- `InvokeOnDrag`：默认开启；
- `DragThreshold`：默认 15 px；
- `FloatingBarAutoHideMs`：默认 1000 ms；
- `NativeFloatingExcludedApps`：排除名单；
- `NativeFloatingIncludeImages`：第一版建议关闭，仅处理文本选区。

交付：设置模型、JSON 归一化、设置页控件、旧配置默认值迁移、开关单测。

### Phase 1：Raw Input 手势服务

新增：

- `src/ClipBar.App/Platform/RawMouseInputService.cs`
- `src/ClipBar.App/Services/FloatingGestureController.cs`

职责：

- 在 ClipBar 主窗口 HWND 上注册鼠标 Raw Input；
- 解析左键 down/up/move；
- 维护 `Pressed/Dragging/HoldTriggered` 状态；
- 回调只做轻量状态更新，不做 UIA、不访问 SQLite、不创建窗口；
- 所有 WPF 操作通过 Dispatcher；
- Dispose 时注销 Raw Input；
- 注册失败写日志并自动降级，不影响主面板、托盘和粘贴。

RED 测试：

- down + 600 ms 无移动触发 hold；
- 任何超过 15 px 的移动取消 hold；
- down/up 不形成拖选时不触发选区流程；
- Dispose 后不再触发回调；
- 快速重复 down/up 不产生重复浮动条。

### Phase 2：选区探测服务

新增：

- `src/ClipBar.App/Services/SelectionProbe.cs`

接口建议：

```csharp
Task<SelectionProbeResult> TryGetSelectionAsync(
    IntPtr foregroundWindow,
    CancellationToken cancellationToken);
```

实现顺序：

1. `AutomationElement.FromHandle` / 当前焦点元素；
2. `TextPattern` 10014；
3. TextPattern2 10024；
4. `GetSelection().GetText(-1)`；
5. 文本为空时，按设置选择是否做 Ctrl+C fallback；
6. fallback 必须内存备份剪贴板格式，超时约 800 ms，失败恢复并返回空；
7. UIA 查询放后台线程，硬超时；
8. 拒绝 ClipBar 自己、SnipDo、密码管理器、提权前台和全屏窗口。

安全规则：

- 不把选区正文写日志；
- 不截图真实选区；
- 不把真实剪贴板内容写入持久证据；
- 读取失败就不显示浮动按钮。

RED 测试：

- 文本框 TextPattern 正常选区；
- 空选区；
- 不支持 TextPattern；
- UIA 超时；
- Ctrl+C fallback 成功/失败/超时；
- 剪贴板格式完整恢复；
- 自进程、提权、全屏目标拒绝。

### Phase 3：浮动操作条

新增：

- `src/ClipBar.App/FloatingActionBarWindow.xaml`
- `src/ClipBar.App/FloatingActionBarWindow.xaml.cs`
- `src/ClipBar.App/Services/FloatingBarCoordinator.cs`

实现原则：

- 普通 `Window`，`WindowStyle=None`；
- `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`；
- 不依赖 WPF-UI FluentWindow 隐式模板；
- `FrameBorder`、主题表面、DWM 圆角沿用现有方案；
- 内容只保留一个 Clipboard 按钮，后续可扩展文本动作；
- `MouseEnter` 后启动 200 ms debounce；
- 鼠标离开按钮前取消展开；
- 位置贴近选区/鼠标点，按当前显示器 DPI 和工作区钳制；
- 选区附近放不下时自动翻转到上方/下方；
- 不抢前台，不改变目标窗口；
- 首帧使用 `FlashFreeShow`，避免白闪/黑边。

生命周期：

- 一个目标窗口对应一个浮动条；
- 新手势关闭旧浮动条；
- 外部点击、Esc、目标窗口切换、全屏检测均关闭；
- 关闭时注销 OutsideClickHook/计时器；
- 不允许多个条叠加。

### Phase 4：复用并改造 CompactPanelWindow

当前 `CompactPanelWindow` 已经可以展示历史和粘贴，但需要增加“作为浮动条子面板”的模式：

- 接收 parent floating bar；
- `ShowBelow(FloatingActionBarWindow)`，而不是只按鼠标位置；
- 保持鼠标从按钮移动到列表的路径；
- 统一“主条或列表任一处悬停则不关闭”；
- 鼠标离开两者后约 1 秒关闭；
- 点击行使用手势开始时捕获的目标窗口；
- 目标失效时复制到剪贴板但不盲目注入 Ctrl+V；
- Esc/外部点击统一关闭；
- 支持文本优先，图片/文件按设置逐步加入。

### Phase 5：设置、可访问性与多 DPI

设置页增加：

- 原生浮动交互总开关；
- 长按开关和时长；
- 拖选开关；
- 排除应用列表；
- 是否显示文本选区工具条；
- 是否启用剪贴板紧凑面板。

A11y：

- 浮动按钮有 UIA Name/Role；
- 紧凑列表行有可读 Name；
- 键盘可进入、Esc 可退出；
- 不依赖悬停才能完成粘贴。

DPI：

- 100/125/150/200%；
- 双屏混合 DPI；
- 选区在屏幕边缘；
- 负坐标屏幕；
- 任务栏/托盘附近；
- 浮动条翻转和钳制。

### Phase 6：暴力测试与长期稳定性

场景：

- 连续 500 次长按/取消；
- 连续 500 次拖选/无选区；
- 浮动条与紧凑列表快速开关；
- 目标窗口快速切换；
- 应用睡眠/唤醒；
- 主题深浅切换；
- 150% 与 200% DPI；
- 目标窗口被关闭/提权/UIPI；
- 剪贴板被占用；
- UIA 超时；
- 进程强杀后重启；
- 30 分钟驻留观察句柄、线程、工作集、钩子释放。

门禁：

- 钩子回调无阻塞；
- 无重复窗口；
- 无未释放计时器/线程/句柄；
- 不改变系统剪贴板格式；
- 不误粘贴到 ClipBar 自身；
- 失败只记录日志，不让主面板或托盘崩溃。

## 5. 测试矩阵与验收标准

### 单元测试

- Raw Input 状态机；
- 600 ms 长按与 15 px 阈值；
- 200 ms hover debounce；
- 显示器边界钳制；
- UIA 结果解析；
- 超时/取消；
- 排除名单；
- 目标窗口守卫。

### 隔离运行测试

- 每个测试使用新的 `--data-dir`；
- 不读取真实 `%LOCALAPPDATA%\\ClipBar` 历史；
- 不使用真实剪贴板正文作为持久证据；
- 目标输入框使用人工 WinForms/WPF 夹具；
- 记录 JSON 结果与 PrintWindow/屏幕截图。

### 真实 GUI 验收

必须逐项验证：

1. 长按左键显示小操作条；
2. 鼠标悬停 Clipboard 按钮约 200 ms 展开紧凑列表；
3. 鼠标从按钮移动到列表不闪退；
4. 点击文本行粘贴到原窗口；
5. Esc/外部点击关闭；
6. 拖选文本后显示操作条；
7. UIA 不支持时不弹错误按钮；
8. 主题、DPI、双屏和全屏组合正常；
9. 现有主面板/托盘/设置功能无回归。

### 发布门禁

每个阶段完成后必须：

```text
RED 回归
→ GREEN 修复
→ 目标测试
→ 全量测试
→ 双发布构建
→ 真实 GUI 复验
→ 编码门禁
→ dist hash
→ git diff/status
```

当前基线为 **478/478**；新增功能后必须保持全绿，并新增浮动交互测试，不以“能弹出来”代替完整验收。

## 6. Git 与发布流程

1. 当前已上传的 `main` 保持稳定，不直接在其上长时间试验；
2. 新阶段使用独立分支，例如 `feature/native-floating-clipboard`；
3. 按阶段提交：
   - `feat: add raw input gesture state machine`
   - `feat: add selection probe with UIA and clipboard fallback`
   - `feat: add floating action bar`
   - `feat: connect compact clipboard panel`
   - `test: add native floating interaction matrix`
4. 每个阶段测试通过后再 push；
5. 失败时可回滚到上一阶段 commit；
6. 发布目录和 `bin/obj` 保持本地忽略，不上传 GitHub；
7. 真实用户数据、真实剪贴板正文、密钥和运行日志不进入仓库。

## 7. 第一阶段的明确交付物

第一阶段只实现 SnipDo 真实链路的最小闭环：

```text
Raw Input 左键长按 600ms
→ 无移动、目标允许
→ ClipBar 浮动操作条
→ Clipboard 按钮 MouseEnter 200ms
→ 现有 CompactPanelWindow
→ 点击历史行
→ PasteService 粘贴
```

第一阶段不同时加入 OCR、复杂选区工具条、图片编辑、云同步或新的扩展系统，避免把验证面扩大到无法定位问题。

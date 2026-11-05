# ClipBar 1.1.0 差分审查发现（Round 3）

> 模式 `differential` · 审计 only · 基线 420 例全绿 · 2026-11-05
> 结果：**S1 × 1、S2 × 2、S3 × 3**；其余维度通过或未验证（见 §3/§4）。

## 1. 发现清单

### F-01（S1）筛选行溢出：时间芯片与来源下拉在默认尺寸就不可达

- **症状**：v1.1.0 新增的「今天 / 7 天」芯片和「来源应用」下拉在默认 420 DIP 宽度下被裁出窗口右侧，
  且没有任何方式滚动过去；最小宽度 360 DIP 下「7 天」芯片整个不可见。
- **实测**（当前屏 DPI 192，scale 2.0）：
  - 默认 420 DIP（窗口 840px）：来源下拉 `relX=882px, w=224px, right=1106px` → 超出窗口 **266px（133 DIP）**；
    「7 天」芯片 `right=854px` → 超出 14px（7 DIP）。
  - 最小 360 DIP（窗口 720px）：「7 天」`offscreen=True`，下拉仍越界。
  - 滚轮 3 格后所有矩形无变化 → 横向滚动实际不可用（`HorizontalScrollBarVisibility="Hidden"` 且无可见提示）。
- **根因**：筛选条内容宽约 565 DIP（4 个类型芯片 + 3 个时间芯片 + 112 DIP 下拉），
  视口仅 ~396 DIP；外层 `ScrollViewer` 隐藏滚动条且不响应滚轮，等于把新功能放在窗口外。
- **证据**：`MainWindow.xaml` 筛选行（ScrollViewer + StackPanel 横排）；UIA 测量脚本输出（T-01~T-03）。
- **修复方向**：改为**两行固定高度**（第一行类型芯片，第二行时间芯片 + 来源下拉），
  两行都不换行；行高 40 → 76 DIP，并在 360 DIP 下加一条布局断言（UIA/探针：所有筛选控件 `right ≤ 视口`）。

### F-02（S2）「图片另存为」会把面板藏掉

- **症状**：右键图片 →「图片另存为…」→ 系统保存对话框出现，面板在对话框后面自动隐藏；
  取消对话框后面板仍不回来，需要重新唤出。
- **机制**：`SaveImageAs` 直接 `dialog.ShowDialog(this)`，未包 `SuppressAutoHide`；
  同时菜单 `Closed` 的 80ms 判定发现前台不是面板（是保存对话框）→ `Hide()`。
  与已实测的 D-C-01（确认弹窗藏面板）同一条链路，只是新功能没复用修复后的 `ConfirmOwned` 模式。
- **证据**：`MainWindow.xaml.cs` `SaveImageAs`（无抑制）；`OpenClipContextMenu` 的 Closed 延迟判定。
  端到端 UIA 复现未完成（自定义菜单窗在外部 UIA 下按钮定位失败），状态记为 code-confirmed。
- **修复方向**：`SaveImageAs` 用 `using var scope = SuppressAutoHide();` 包住 `ShowDialog`，
  结束后 `ForceForeground()`；与 `ConfirmOwned/NotifyOwned` 统一。

### F-03（S2）恢复面板位置时用 DIP 调用 `MonitorFromPoint`

- **症状**：多显示器 + 不同缩放时，保存的 DIP 坐标被当作物理像素用于选择显示器，
  可能在屏边界附近选中错误的显示器，进而按错误的工作区夹取，面板落在意外位置。
- **证据**：`MainWindow.xaml.cs` `RestorePlacement`：`new NativeMethods.Point(saved.PanelLeft, saved.PanelTop)`
  → `MonitorFromPoint`（该 API 期望物理屏幕坐标）；`NotePanelGeometry` 保存的是 `Left/Top`（DIP）。
- **范围**：单显示器不受影响；本机单屏无法运行时复现 → 状态 `unverified-runtime`（API 契约已确认）。
- **修复方向**：保存时同时存物理坐标（`DIP × DpiScale`），或恢复时先用当前 `DpiScale` 换算再选屏，
  选屏后再用该屏 scale 夹取。

### F-04（S3）来源下拉的当前选择会被静默重置

- **症状**：选了某个来源后切换类型筛选/搜索，使该来源暂时没有结果 → 下拉选项被重建，
  当前值悄悄回到「全部来源」，用户以为筛选失效。
- **证据**：`MainViewModel.UpdateSourceOptions`：`SourceOptions.Clear()` 后若 `!Contains(SelectedSource)`
  直接 `SelectedSource = "全部来源"`。
- **修复方向**：保留选中项作为附加选项（标注「无匹配」），或在状态栏提示「当前来源无结果」。

### F-05（S3）非法 `--data-dir` 的诊断路径不可靠

- **症状**：`--data-dir "C:\bad<name"` 启动后进程 4 秒未退出、没有新增日志（被强制结束后无法确认最终状态）。
- **原因**：`AppPaths.Initialize` 在 `OnStartup` 的 try 之外；随后 `EnsureCreated` 失败 → 进入启动失败弹窗；
  而 `AppLog` 也写入同一坏目录 → 日志静默失败，只剩一个弹窗。
- **证据**：实测 T-06；`App.xaml.cs` Initialize 位置；`AppLog.Write` 依赖 AppPaths。
- **修复方向**：`--data-dir` 先校验（可创建 + 可写），失败立即回退默认目录并写日志；
  日志初始化提供 `%TEMP%` 兜底路径。

### F-06（S3，设计说明）「今天」过滤不跨午夜自动刷新

- 过滤在刷新时计算；跨过午夜后已显示的列表不会自动变化，直到下一次刷新/操作。
- 属可接受行为，但应在用户可见处注明或加午夜定时刷新（低优先级）。

## 2. 通过项（证明覆盖过）

| 项 | 证据 |
| --- | --- |
| 全量测试 | 420 例全绿（App 232 + Core 188） |
| 编码门禁 | 退出码 0 |
| 产物完整性 | `dist/ClipBar.exe` hash `3B81D9…ECDD` 与发布一致 |
| 边缘缩放 | 右缘拖动 720→780px，用户可缩放 |
| 几何持久化 | 测试中改尺寸后 `HasPanelPosition=true / 360×480` 落盘；结束已还原 420×620 居中 |
| schema 迁移 | 新库 user_version=2、旧库升级不丢行（单测 2 例） |
| 删除撤销 | trash 迁移/恢复/清扫 3 例 + VM 撤销 1 例 |
| 纯文本菜单 | 进程内探针：文本条目有「粘贴为纯文本」，图片条目有「图片另存为…」且互斥 |
| stdin JSON | PowerShell 实跑读取 filePaths；JSON 结构单测；UTF-8/BOM 坑已修 |
| 动作注册表 | 注册即可运行 1 例 |
| 高亮/过滤纯函数 | 4 + 6 例 |
| 新控件可访问性 | 来源下拉有 `AutomationProperties.Name`；菜单项为文本按钮 |

## 3. 未验证（不标 pass）

| 项 | 原因 | 复验条件 |
| --- | --- | --- |
| F-02 端到端 | 外部 UIA 无法定位自定义菜单按钮 | 用进程内探针或人工点击 |
| F-03 多屏 | 本机单显示器 | 双屏 / 混合 DPI 环境 |
| F-05 最终状态 | 进程被强杀 | 不杀进程，等待弹窗出现并截图 |
| MaxItems=5000 性能 | 本轮 L2 不做 L3 压测 | 5k 行 + 虚拟化滚动探针 |
| 图片另存为写入失败分支 | 需要只读目标目录 | 只读目录实测 |

## 4. 覆盖统计

- 检查点：13 维全部有状态（pass 9 / fail 3 / unverified 1，其中 fail 分布在正确性、UI、UX）。
- 矩阵：6 条（T-01~T-06），5 条有实测输出，1 条 code-confirmed。

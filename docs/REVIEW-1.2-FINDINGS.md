# REVIEW-1.2 发现与修复记录

静态车道（正确性+并发 / UX+一致性）与实测车道（功能矩阵 / 压力）的全部发现。修复均已在
HEAD 完成；实测证据见 REVIEW-1.2-REPORT。

## 静态车道 A（正确性 + 并发，L3）

| ID | 严重度 | 位置 | 问题 | 状态 |
| --- | --- | --- | --- | --- |
| R12-F-01 | S1 | MainWindow.xaml.cs:172 | ShowPanel 的 fire-and-forget 任务无观察者，show 序列异常被静默丢弃 | 已修：ShowPanelCore 拆分 + try/catch 记录 |
| R12-F-02 | S3 | MainWindow.xaml.cs:204 | 旧版 DIP 坐标换算用首显前的 DpiScale（恒 1.0） | 已修：用落点显示器缩放换算 |
| R12-F-03 | S2 | MainWindow.xaml.cs:128 | 面板可见时退出，几何延迟保存被关机中止 | 已修：OnClosing 真关闭时同步保存 |
| R12-F-04 | S2 | App.xaml.cs:330 | 隐藏时捕获刷新失败弹无主模态框打断用户 | 已修：NotifyOwned 隐藏时降级为日志 |
| R12-F-05 | S3 | MainWindow.xaml.cs:177 | GetCursorPos 失败按 (0,0) 定位 | 已修：回退主屏工作区中心 |
| R12-F-06 | S3 | MainWindow.xaml.cs:128 | idle 几何保存与重开竞态，把自动位置存成用户位置 | 已修：geometry epoch 失效旧保存 |
| R12-F-07 | S3 | TrayService.cs:98 | 托盘菜单 ShowAt 无异常保护 | 已修：与面板侧一致的 try/catch + 关闭 |
| R12-F-08 | S3 | ThemeService.cs:162 | RestoreLater 死代码 | 已删 |
| R12-F-09 | S3 | MainViewModel.cs:240 | IsLoaded 只写不读 | 已删（门时序本身保证正确性） |
| R12-F-10 | S3 | App.xaml.cs:397 | SaveSettings 副作用无保护，失败后 _applied 与运行态不一致 | 已修：try/catch + 明确消息 |

## 静态车道 B（UX / 布局 / 一致性）

| ID | 严重度 | 问题 | 状态 |
| --- | --- | --- | --- |
| R12-U-01 | S3 | 来源菜单不标记当前选中项 | 已修：当前项加粗 + 对勾图标 |
| R12-U-02 | S3 | footer 来源角标与漏斗字形压 1 DIP | 已修：角标 Margin 0,2,1,0 |
| R12-U-03 | S3 | 撤销条贴列表末行分割线 | 已修：Margin 12,4,10,8 |
| R12-U-04 | S3 | 固定图标硬编码 #60A5FA/#8A8A8A 脱离强调色体系 | 已修：DataTrigger 用 SystemAccentColorBrush |
| R12-U-05 | S3 | 搜索高亮画刷永久缓存，换主题不更新 | 已修：按颜色缓存 |
| R12-U-06 | S3 | 菜单内置图标 14 与扩展图标 16 不齐 | 已修：FontSize 16 |
| R12-U-07 | S3 | 死样式 FooterButton | 已删 |
| R12-U-08 | S3 | IconButton 缺 pressed 态 | 已修：补 Tertiary 触发器 |
| R12-U-09 | S3 | 冗余 StackPanel 包装 / 无效 Margin | 已修 |
| R12-U-10 | S3 | 「清空」三处叫法不一 | 已修：托盘改「清空全部历史」 |
| R12-U-11 | S3 | 面板位置缺解释行 | 已修：补 Hint |
| R12-U-12 | S3 | 空状态「（如已导入）」含糊 | 已修：改为「需先在设置中导入」 |
| R12-U-13 | S3 | 设置页控件缺 UIA 名称 | 已修：6 个控件补齐 |
| R12-U-14 | S3 | AppOptionWindow 下拉缺名称 | 已修 |
| R12-U-15 | S3 | 透明度 0 的按钮仍在 UIA 树 | 保留（改 Hidden 有布局回归风险，行为正确） |
| R12-U-16 | S3 | GOAL.md 缺 v1.1.2 记录且默认值过时 | 已修 |
| R12-U-17 | S3 | PANEL-LIFETIME-RULES 序号重复 | 已修 |
| R12-U-18 | S3 | README 两处列举不全 | 已修 |

## 实测车道发现

| ID | 严重度 | 问题 | 状态 |
| --- | --- | --- | --- |
| R12-F-11 | S2 | 右键菜单行 UIA Name 为空（图标行内容不传播名称）：读屏读空行、UIA 客户端枚举不到 | 已修：AutomationProperties.SetName；实测菜单行名可枚举 |
| R12-F-12 | S2 | 前台为 0（点击桌面后）时延迟失焦检查把面板留下 | 已修（上轮提交 17c0f53 内）：0 按外部处理 |

## 通过清单（摘要）

正确性 10 个审查点全部通过（single-flight 双重闸、三模式数学防护、钩子生命周期、抑制域幂等、
设置草稿基线、主题订阅无重入、菜单关闭分支、ViewModel 版本守卫、App 启动/退出路径）。
UX：footer 三列 360–2000 DIP 无换行、来源按钮 12/10 对齐线、图标字号体系、硬编码颜色扫描干净、
位置术语三处一致、可访问性主体覆盖良好。
压力：热键/Esc 切换 20/20、连续外部点击 10/10 存活、菜单外点循环无泄漏。

# ClipBar

面向 Windows 的本地剪贴板历史工具，提供 Win+V 风格的历史管理和可选浮动操作栏。

## 构建与运行

需要 Windows 和 .NET 10 SDK。在仓库根目录运行：

```cmd
build.cmd
```

生成的自包含单文件位于 `dist\ClipBar.exe`；若系统已安装 .NET 10 Desktop Runtime，也可使用 `dist-lite\ClipBar.exe`。
首次运行会在用户本地应用数据目录创建 ClipBar 数据文件。

## 功能

- 捕获文本、富文本 HTML、图片和文件列表，并按内容去重。
- 搜索历史、按类型筛选、固定条目、删除与撤销删除。
- 支持图片预览、主题切换、可配置热键和面板位置。
- 可选浮动操作栏；支持托盘暂停捕获、开机启动与粘贴行为设置。

## 安全与限制

ClipBar 将剪贴板内容、设置和文件路径视为不可信输入。它不会执行剪贴板中的脚本或插件代码；向提权窗口不会注入 Ctrl+V，用户可以在目标窗口手动粘贴。

发现安全问题请通过 GitHub 仓库的 **Security → Report a vulnerability** 私下报告，不要在公开 Issue 中发布可利用细节。

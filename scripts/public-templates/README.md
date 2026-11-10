# ClipBar

面向 Windows 的本地剪贴板历史工具，提供历史搜索、类型筛选、固定与删除撤销，以及可选浮动操作栏。

## 安装

从仓库的 Releases 页面下载标准版或轻量版；标准版可直接运行，轻量版需要 .NET 10 Desktop Runtime。首次运行会在用户本地应用数据目录创建 ClipBar 数据文件。

## 用法

通过可配置热键或托盘打开历史面板，搜索或筛选后选择条目复制或粘贴；主题、图片预览、来源排除、开机启动和浮动操作栏可在设置中调整。

### 从源码构建

需要 Windows、PowerShell 和 .NET 10 SDK。在仓库根目录打开 PowerShell：

```powershell
$workspace = (Get-Item .).Parent.FullName
.\build.cmd (Join-Path $workspace 'clipbar-build') $workspace
```

输出目录必须尚不存在，且与源码目录分离。自包含单文件位于输出的 `standard\ClipBar.exe`，轻量版位于 `lite\ClipBar.exe`，发行附件与校验文件位于 `release-assets\`；脚本不会覆盖运行中的程序或数据目录。也可以直接调用 `scripts/package.ps1`，用可选 `PackageSource` 指定本地 NuGet 源。

## 已知限制

- 剪贴板内容、设置和文件路径均视为不可信输入，不执行剪贴板中的脚本或插件代码。
- 普通权限实例不会向提权窗口注入 Ctrl+V；目标不适合自动粘贴时，可手动粘贴已复制的内容。
- 历史可能包含敏感内容，请按需暂停捕获、配置来源排除或清空历史。

## 安全

发现安全问题请通过 GitHub 仓库的 **Security → Report a vulnerability** 私下报告，不要在公开 Issue 中发布可利用细节。

## 贡献

欢迎提交问题和改进建议；报告问题时请移除真实剪贴板内容和其他个人信息。

## 许可

MIT，见 [LICENSE](LICENSE)。

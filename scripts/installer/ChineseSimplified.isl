; ClipBar installer language: Simplified Chinese.
; Only the messages a user actually sees are translated; Inno Setup falls back to Default.isl
; (English) for anything missing, so this file stays small and safe to maintain.

[LangOptions]
LanguageName=简体中文
LanguageID=$0804
LanguageCodePage=936

[Messages]
SetupAppTitle=安装程序
SetupWindowTitle=安装程序 - %1
UninstallAppFullTitle=卸载 %1
ExitSetupTitle=退出安装
ExitSetupMessage=安装尚未完成。如果现在退出，程序将不会被安装。%n%n你可以稍后重新运行安装程序来完成安装。%n%n确定退出吗？
ButtonBack=< 上一步(&B)
ButtonNext=下一步(&N) >
ButtonInstall=安装(&I)
ButtonCancel=取消
ButtonFinish=完成(&F)
ClickNext=点击「下一步」继续，或点击「取消」退出安装。
WelcomeLabel1=欢迎使用 [name] 安装向导
WelcomeLabel2=这将在你的电脑上安装 [name/ver]。%n%n建议在继续之前关闭其他正在运行的程序。
SelectDirDesc=[name] 应该安装到哪里？
SelectDirLabel3=安装程序将把 [name] 安装到下面的文件夹。
SelectDirBrowseLabel=点击「下一步」继续；如果想换一个文件夹，点击「浏览」。
DiskSpaceMBLabel=至少需要 [mb] MB 的可用磁盘空间。
SelectTasksDesc=需要执行哪些附加任务？
SelectTasksLabel2=选择安装 [name] 时要一并执行的附加任务，然后点击「下一步」。
ReadyLabel1=安装程序已准备好，将开始在电脑上安装 [name]。
ReadyLabel2a=点击「安装」开始安装；如果想检查或修改设置，点击「上一步」。
PreparingDesc=正在准备安装 [name]。
InstallingLabel=正在安装 [name]，请稍候。
FinishedHeadingLabel=正在完成 [name] 安装向导
FinishedLabel=已在电脑上完成 [name] 的安装。可以通过开始菜单中的快捷方式启动它。
ClickFinish=点击「完成」退出安装程序。
FinishedRestartLabel=要完成 [name] 的安装，必须重启电脑。现在重启吗？
ConfirmUninstall=确定要完全删除 %1 及其所有组件吗？%n%n剪贴板历史与设置保存在 %LOCALAPPDATA%\ClipBar，不会被删除。
UninstalledAll=%1 已从电脑上成功删除。
AdditionalIcons=附加快捷方式：
CreateDesktopIcon=创建桌面快捷方式(&D)
LaunchProgram=启动 %1

[CustomMessages]
AutoStartProgram=开机时自动启动 %1
AutoStartProgramGroupDescription=启动
AssocingFileExtension=正在关联 %1 文件...
AddonHostProgramNotFound=%1 未找到，请手动指定它的位置。

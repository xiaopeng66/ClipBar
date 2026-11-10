@echo off
setlocal
set PYTHONUTF8=1
set PYTHONIOENCODING=utf-8

if "%~2"=="" goto usage
powershell.exe -NoLogo -NoProfile -NonInteractive -File "%~dp0scripts\package.ps1" ^
  -ProjectRoot "%~dp0." -OutputDirectory "%~1" -WorkspaceRoot "%~2"
if errorlevel 1 exit /b 1
exit /b 0

:usage
echo Usage: build.cmd ^<new-output-directory^> ^<workspace-root^>
echo Use absolute Windows paths. The output must not exist or overlap the source.
echo Both paths must be inside the selected workspace. No running files or data are replaced.
exit /b 2

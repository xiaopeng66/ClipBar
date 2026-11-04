@echo off
setlocal
chcp 65001 >nul
set PYTHONUTF8=1
set PYTHONIOENCODING=utf-8

cd /d "%~dp0"

echo === tests ===
dotnet test -v q
if errorlevel 1 goto fail

echo === publish self-contained single file (about 70 MB) ===
dotnet publish src\ClipBar.App -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o dist
if errorlevel 1 goto fail

echo === publish framework-dependent single file (about 9 MB, needs .NET 10 Desktop Runtime) ===
dotnet publish src\ClipBar.App -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -o dist-lite
if errorlevel 1 goto fail

echo.
echo Done:
echo   dist\ClipBar.exe       self-contained, about 70 MB
echo   dist-lite\ClipBar.exe  framework-dependent, about 9 MB
goto eof

:fail
echo.
echo Build failed, see the output above.
exit /b 1

:eof

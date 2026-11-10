@echo off
setlocal
chcp 65001 >nul
set PYTHONUTF8=1
set PYTHONIOENCODING=utf-8

cd /d "%~dp0"

echo === publish self-contained single file ===
dotnet publish src\ClipBar.App -c Release -r win-x64 --self-contained ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o dist
if errorlevel 1 goto fail

echo === publish framework-dependent single file ===
dotnet publish src\ClipBar.App -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=false -o dist-lite
if errorlevel 1 goto fail

echo.
echo Done:
echo   dist\ClipBar.exe       self-contained single file
echo   dist-lite\ClipBar.exe  requires .NET 10 Desktop Runtime
goto eof

:fail
echo.
echo Build failed, see the output above.
exit /b 1

:eof

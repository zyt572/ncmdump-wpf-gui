@echo off
setlocal
cd /d "%~dp0"

dotnet publish NcmGui\NcmGui.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None ^
  -o dist

if errorlevel 1 (
  echo Publish failed.
  pause
  exit /b 1
)

echo.
echo Done. Output: dist\NcmGui.exe
pause

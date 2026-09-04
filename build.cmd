@echo off
setlocal
cd /d "%~dp0"
set SC=false
if /I "%~1"=="--self-contained" set SC=true
echo Building Roch CPU (self-contained=%SC%)...
dotnet publish src\RochPower\RochPower.csproj -c Release -r win-x64 --self-contained %SC% -p:PublishSingleFile=%SC% -o dist
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo.
echo Output: %~dp0dist\Roch CPU.exe
endlocal

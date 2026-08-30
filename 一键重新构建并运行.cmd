@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 8 SDK was not found. Please install it first.
    pause
    exit /b 1
)

echo Building PixivRanker in Release mode...
dotnet build ".\PixivRanker.sln" -c Release -t:Rebuild --nologo
if errorlevel 1 (
    echo.
    echo Build failed. See the errors above.
    pause
    exit /b 1
)

echo Build succeeded. Starting PixivRanker...
start "" "%~dp0src\PixivRanker\bin\Release\net8.0-windows\PixivRanker.exe"
exit /b 0

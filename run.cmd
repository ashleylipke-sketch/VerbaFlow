@echo off
setlocal
cd /d "%~dp0"
echo === VerbaFlow: checking tools ===

where dotnet >nul 2>nul || (echo [X] .NET SDK not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download then open a NEW Command Prompt. & goto fail)
for /f "delims=" %%v in ('dotnet --version') do set DOTNETV=%%v
echo .NET SDK: %DOTNETV%
echo %DOTNETV% | findstr /b "10." >nul || (echo [X] .NET 10 is required but you have %DOTNETV%. Install the .NET 10 SDK. & goto fail)

where node >nul 2>nul || (echo [X] Node.js not found. Install Node.js 20 or newer from https://nodejs.org then open a NEW Command Prompt. & goto fail)
for /f "delims=" %%v in ('node --version') do echo Node.js: %%v

echo.
echo === Building the web app ===
pushd web
call npm install || (popd & echo [X] npm install failed. & goto fail)
call npm run build || (popd & echo [X] Web build failed. & goto fail)
popd

echo.
echo === Starting VerbaFlow ===
echo When you see "Now listening on", open http://localhost:5044 in Chrome or Edge.
echo Leave this window open while you use the app. Press Ctrl+C to stop.
echo.
cd src\VerbaFlow.Api
dotnet run --launch-profile http
if errorlevel 1 goto fail
exit /b 0

:fail
echo.
echo Something went wrong. Copy the messages above and send them to Claude.
pause
exit /b 1

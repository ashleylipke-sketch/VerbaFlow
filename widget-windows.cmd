@echo off
setlocal
cd /d "%~dp0"
rem Installs (or updates) the VerbaFlow Widget for Windows for the person signed in to this computer, then starts it.
echo === VerbaFlow Widget for Windows ===

if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"
where dotnet >nul 2>nul || (echo [X] .NET SDK not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download then open a NEW Command Prompt. & goto fail)

set "TARGET=%LOCALAPPDATA%\VerbaFlow\Widget\app"
echo Closing the Widget if it is running...
taskkill /im VerbaFlowWidget.exe /f >nul 2>nul
timeout /t 1 /nobreak >nul

echo Building the Widget...
dotnet publish widget\windows\VerbaFlow.Widget\VerbaFlow.Widget.csproj -c Release --self-contained false -o "%TARGET%" -nologo -v quiet
if errorlevel 1 (echo [X] The Widget could not be built. & goto fail)

echo Adding it to the Start menu...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\VerbaFlow Widget.lnk'); $s.TargetPath='%TARGET%\VerbaFlowWidget.exe'; $s.WorkingDirectory='%TARGET%'; $s.Description='Speak into any Windows program with VerbaFlow (Alt+Shift+D)'; $s.Save()"

echo Starting it...
start "" "%TARGET%\VerbaFlowWidget.exe"
echo.
echo Done. The VerbaFlow icon is near the clock (click the ^ arrow if you cannot see it).
echo Click where you want the words in any program, press Alt+Shift+D and speak.
echo Next time, start it from the Start menu: VerbaFlow Widget.
pause
exit /b 0

:fail
echo.
echo Something went wrong. Copy the messages above and send them to Claude.
pause
exit /b 1

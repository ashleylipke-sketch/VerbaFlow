@echo off
setlocal
cd /d "%~dp0"
rem Opens a private, secure (https) address for VerbaFlow so you can use it on your phone.
rem VerbaFlow itself must already be running: start run.cmd in another window first.
echo === VerbaFlow on your phone ===

where devtunnel >nul 2>nul || (
  echo Installing Microsoft Dev Tunnels ^(one time only^)...
  winget install --id Microsoft.devtunnel -e --accept-source-agreements --accept-package-agreements || (echo [X] Could not install Dev Tunnels. & goto fail)
  echo.
  echo Dev Tunnels is installed. Close this window, open a NEW Command Prompt and run phone.cmd again.
  pause
  exit /b 0
)

rem Signing in once with your Microsoft account. Only this account can open the address.
devtunnel list >nul 2>nul || (
  echo A browser window opens: sign in with your Microsoft account.
  devtunnel user login || (echo [X] Sign-in to Dev Tunnels did not finish. & goto fail)
)

rem The same address is kept between runs (saved in phone-tunnel.txt) until it expires after 30 days.
set "TUNNEL="
if exist phone-tunnel.txt set /p TUNNEL=<phone-tunnel.txt
if defined TUNNEL (
  devtunnel show %TUNNEL% >nul 2>nul || set "TUNNEL="
)
if not defined TUNNEL (
  set "TUNNEL=verbaflow-%RANDOM%%RANDOM%"
  call :create || goto fail
)

echo.
echo On your phone:
echo   1. Open the address shown below after "Connect via browser" ^(the one ending in devtunnels.ms^).
echo   2. Sign in with the SAME Microsoft account you used here ^(only you can open it^).
echo   3. Choose Continue, then use VerbaFlow as on the computer.
echo Leave this window and the run.cmd window open while you use it. Press Ctrl+C here to close the address.
echo.
devtunnel host %TUNNEL%
exit /b 0

:create
devtunnel create %TUNNEL% --expiration 30d || (echo [X] Could not create the address. & exit /b 1)
devtunnel port create %TUNNEL% -p 5044 --protocol http || (echo [X] Could not add VerbaFlow to the address. & exit /b 1)
>phone-tunnel.txt echo %TUNNEL%
exit /b 0

:fail
echo.
echo Something went wrong. Copy the messages above and send them to Claude.
pause
exit /b 1

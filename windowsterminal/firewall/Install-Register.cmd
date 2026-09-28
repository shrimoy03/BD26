@echo off
rem WinkPay Register - install / update on this PC (run as Administrator, once per new build).
rem
rem What it does, and why each step matters for a STANDARD user afterwards:
rem   1. Copies this folder to "%ProgramFiles%\WinkPay Register". Default
rem      AppLocker / Software Restriction rules let any user run programs
rem      under Program Files and block them under Downloads, Desktop, TEMP.
rem   2. Removes the Mark-of-the-Web from every file. A zip that came from a
rem      browser, Slack, Drive or Teams carries it, Explorer stamps it onto
rem      every extracted file, and SmartScreen blocks unsigned exes that carry
rem      it. Without the mark SmartScreen does not even look.
rem   3. Creates Start Menu + Desktop shortcuts to the installed exe. Pin the
rem      taskbar from THAT shortcut - a pin made from an exe in Downloads
rem      keeps pointing at the blocked copy.
rem   4. Adds the inbound firewall rules (Allow-Register-Firewall.cmd).
rem
rem The register itself never needs Administrator: it declares "asInvoker",
rem writes settings and logs to %APPDATA%, and listens on 8080/8181 only.

setlocal
set "SRC=%~dp0"
set "DEST=%ProgramFiles%\WinkPay Register"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo This must run as Administrator: right-click Install-Register.cmd, "Run as administrator".
  pause
  exit /b 1
)

echo Stopping a running register...
taskkill /IM MerchantTerminal.exe /F >nul 2>&1

echo Copying to "%DEST%" ...
robocopy "%SRC%." "%DEST%" /E /R:2 /W:1 /XF *.pdb /NFL /NDL /NJH /NJS >nul
if %errorlevel% geq 8 (
  echo Copy failed ^(robocopy %errorlevel%^).
  pause
  exit /b 1
)

echo Removing the Mark-of-the-Web ...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-ChildItem -LiteralPath '%DEST%' -Recurse -File | Unblock-File"

echo Creating shortcuts ...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s=(New-Object -ComObject WScript.Shell);" ^
  "foreach($p in @([Environment]::GetFolderPath('CommonPrograms'),[Environment]::GetFolderPath('CommonDesktopDirectory'))){" ^
  "  $l=$s.CreateShortcut((Join-Path $p 'WinkPay Register.lnk'));" ^
  "  $l.TargetPath='%DEST%\MerchantTerminal.exe'; $l.WorkingDirectory='%DEST%';" ^
  "  $l.IconLocation='%DEST%\MerchantTerminal.exe,0'; $l.Description='WinkPay Register'; $l.Save() }"

echo Firewall rules ...
call "%DEST%\Allow-Register-Firewall.cmd" /quiet

echo.
echo Installed. Launch "WinkPay Register" from the Start Menu or Desktop as a normal user,
echo then right-click its taskbar icon and choose "Pin to taskbar".
pause
endlocal

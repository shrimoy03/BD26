@echo off
rem WinkPay Register - one-time Windows Firewall rules (run as Administrator).
rem
rem The register LISTENS for two things the terminal sends in:
rem   TCP 8080  PXRRS notify callback (tender pressed, payment result)
rem   TCP 8181  WinkPay app WebSocket (the sale itself)
rem and answers discovery replies on UDP 8182.
rem On a phone hotspot Windows files the network under the Public profile,
rem where inbound connections are blocked by default and a standard user
rem cannot approve the firewall prompt. Run this once from an elevated prompt:
rem   right-click -> Run as administrator
rem
rem Adjust EXE if the register lives somewhere else.

set "EXE=%~dp0MerchantTerminal.exe"
if not exist "%EXE%" set "EXE=%ProgramFiles%\WinkPay Register\MerchantTerminal.exe"
set "QUIET=%~1"

net session >nul 2>&1
if %errorlevel% neq 0 (
  echo This must run as Administrator. Right-click the file and choose "Run as administrator".
  if /i not "%QUIET%"=="/quiet" pause
  exit /b 1
)

netsh advfirewall firewall delete rule name="WinkPay Register (notify 8080)" >nul 2>&1
netsh advfirewall firewall delete rule name="WinkPay Register (WebSocket 8181)" >nul 2>&1
netsh advfirewall firewall delete rule name="WinkPay Register (discovery 8182)" >nul 2>&1
netsh advfirewall firewall delete rule name="WinkPay Register (program)" >nul 2>&1

netsh advfirewall firewall add rule name="WinkPay Register (notify 8080)"    dir=in action=allow protocol=TCP localport=8080 profile=any enable=yes
netsh advfirewall firewall add rule name="WinkPay Register (WebSocket 8181)" dir=in action=allow protocol=TCP localport=8181 profile=any enable=yes
netsh advfirewall firewall add rule name="WinkPay Register (discovery 8182)" dir=in action=allow protocol=UDP localport=8182 profile=any enable=yes
if exist "%EXE%" netsh advfirewall firewall add rule name="WinkPay Register (program)" dir=in action=allow program="%EXE%" profile=any enable=yes

echo.
echo Rules added:
netsh advfirewall firewall show rule name="WinkPay Register (notify 8080)" | findstr /i "Enabled Profiles LocalPort"
netsh advfirewall firewall show rule name="WinkPay Register (WebSocket 8181)" | findstr /i "Enabled Profiles LocalPort"
echo.
echo Done. Restart the register, then on the terminal press Face once to confirm.
if /i not "%QUIET%"=="/quiet" pause

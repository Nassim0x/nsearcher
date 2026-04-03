@echo off
powershell -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
echo %PATH% | find /I "%LOCALAPPDATA%\Programs\NSearcher" >NUL
if errorlevel 1 set "PATH=%PATH%;%LOCALAPPDATA%\Programs\NSearcher"

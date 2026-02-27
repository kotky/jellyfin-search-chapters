@echo off
REM Run the checksum script with ExecutionPolicy Bypass (avoids "running scripts is disabled").
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-release-checksum.ps1" %*

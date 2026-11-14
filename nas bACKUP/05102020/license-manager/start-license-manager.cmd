@echo off
rem Starts the NAS POS Pro License Manager on http://localhost:5300 and opens it. Close this window to stop it.
rem Run it as the Windows user that owns the signing key (it is encrypted for that user).
title NAS License Manager
cd /d "%~dp0NasLicenseManager"
start "" cmd /c "timeout /t 6 >nul & start http://localhost:5300"
dotnet run --no-launch-profile -c Release

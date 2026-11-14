@echo off
rem Starts NAS Chatbot on http://localhost:5090 and opens it in the browser. Close this window to stop it.
title NAS Chatbot
cd /d "%~dp0src\NasChatbot"
start "" cmd /c "timeout /t 4 >nul & start http://localhost:5090"
dotnet run --no-launch-profile -c Release

@echo off
cd /d "%~dp0"
dotnet build kw1281test.csproj -c Debug
echo.
echo === Build complete. Press any key to close. ===
pause

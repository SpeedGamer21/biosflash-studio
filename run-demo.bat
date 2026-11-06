@echo off
REM Starts BiosFlash Studio with the built-in simulator (no programmer needed)
cd /d "%~dp0"
if exist dist\BiosFlashStudio.exe ( start "" dist\BiosFlashStudio.exe --demo ) else ( dotnet run --project src\BiosFlashStudio.csproj -- --demo )

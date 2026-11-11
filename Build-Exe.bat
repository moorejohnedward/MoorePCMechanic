@echo off
title Building Moore PC Mechanic
cd /d "%~dp0"
echo Building Moore PC Mechanic (single .exe)...
dotnet publish "MoorePCMechanic\MoorePCMechanic.csproj" -p:PublishProfile=FolderProfile
if errorlevel 1 (
  echo.
  echo Build failed. Open MoorePCMechanic.sln in Visual Studio to see the details.
  pause
  exit /b 1
)
echo.
echo Done! Your app is in "Release (app to run and share)\MoorePCMechanic.exe"
start "" "Release (app to run and share)"
pause

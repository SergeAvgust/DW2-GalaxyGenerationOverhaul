@echo off
rem Builds SpiralArms (Release) and deploys it into the game's mods folder.
rem Override the game location with:  set GAMEDIR=D:\Games\Distant Worlds 2
setlocal

if "%GAMEDIR%"=="" set "GAMEDIR=C:\Program Files (x86)\Steam\steamapps\common\Distant Worlds 2"
set "MODDIR=%GAMEDIR%\mods\SpiralArms"
set "OUT=%~dp0bin\Release\net8.0"

dotnet build "%~dp0SpiralArms.csproj" -c Release -p:GameDir="%GAMEDIR%"
if errorlevel 1 exit /b 1

if not exist "%MODDIR%" mkdir "%MODDIR%"
copy /y "%OUT%\SpiralArms.dll" "%MODDIR%\" >nul || exit /b 1
copy /y "%OUT%\settings.schema.json" "%MODDIR%\" >nul || exit /b 1
copy /y "%~dp0mod.json" "%MODDIR%\" >nul || exit /b 1

echo Deployed to %MODDIR%

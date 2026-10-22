@echo off
rem One command from a fresh clone to playing Spayly in Godot. Everything is in godot\tools\start.mjs.
rem Usage: start.cmd [--rebuild] [--no-pull] [--prepare-only] [--dry-run] [-- godot args]
cd /d "%~dp0"

where node >nul 2>nul
if errorlevel 1 goto nonode
for /f %%v in ('node -p "process.versions.node.split('.')[0]"') do set NODEMAJOR=%%v
if %NODEMAJOR% LSS 22 goto nonode

node godot\tools\start.mjs %*
if errorlevel 1 pause
exit /b %errorlevel%

:nonode
echo Spayly needs Node 22 or newer to run its launcher. Install it, open a NEW terminal, then run start.cmd again:
echo   winget install OpenJS.NodeJS.LTS
pause
exit /b 1

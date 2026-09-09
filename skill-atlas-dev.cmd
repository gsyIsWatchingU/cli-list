@echo off
setlocal
title Skill Atlas Dev

if /i "%~1"=="--hidden" set "SKILL_ATLAS_HIDDEN_DEV=1"

set "SKILL_ATLAS_DIR=E:\prj-gsy\skill-atlas"

if not exist "%SKILL_ATLAS_DIR%\package.json" (
  echo [ERROR] Skill Atlas project not found: %SKILL_ATLAS_DIR%
  pause
  exit /b 1
)

where npm >nul 2>nul
if errorlevel 1 (
  echo [ERROR] npm was not found. Install Node.js and add npm to PATH.
  pause
  exit /b 1
)

cd /d "%SKILL_ATLAS_DIR%"
echo Starting Skill Atlas in development mode...
echo Keep this window open for live reload. Press Ctrl+C to stop.
echo.
call npm run dev

if errorlevel 1 (
  if "%SKILL_ATLAS_HIDDEN_DEV%"=="1" exit /b 1
  echo.
  echo [ERROR] Skill Atlas failed to start. Check the log above.
  pause
)

endlocal

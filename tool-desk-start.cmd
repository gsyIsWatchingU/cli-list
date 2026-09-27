@echo off
setlocal
title Tool Desk (Interview Copilot)

set "TOOL_DESK_DIR=E:\prj-gsy\tool-desk"
set "ELECTRON_RUN_AS_NODE="
set "ELECTRON_NO_ATTACH_CONSOLE="

if not exist "%TOOL_DESK_DIR%\package.json" (
  echo [ERROR] Tool Desk project not found: %TOOL_DESK_DIR%
  pause
  exit /b 1
)

where npm >nul 2>nul
if errorlevel 1 (
  echo [ERROR] npm was not found. Install Node.js and add npm to PATH.
  pause
  exit /b 1
)

cd /d "%TOOL_DESK_DIR%"
echo Starting Tool Desk (Interview Copilot)...
echo Keep this window open. Press Ctrl+C to stop.
echo.
call npm start

if errorlevel 1 (
  echo.
  echo [ERROR] Tool Desk failed to start. Check the log above.
  pause
)

endlocal

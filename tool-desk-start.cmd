@echo off
setlocal
title Tool Desk (Interview Copilot)

if /i "%~1"=="--hidden" set "TOOL_DESK_HIDDEN_START=1"

set "TOOL_DESK_DIR=E:\prj-gsy\tool-desk"

if not exist "%TOOL_DESK_DIR%\package.json" (
  echo [ERROR] Tool Desk project not found: %TOOL_DESK_DIR%
  if not "%TOOL_DESK_HIDDEN_START%"=="1" pause
  exit /b 1
)

where npm >nul 2>nul
if errorlevel 1 (
  echo [ERROR] npm was not found. Install Node.js and add npm to PATH.
  if not "%TOOL_DESK_HIDDEN_START%"=="1" pause
  exit /b 1
)

cd /d "%TOOL_DESK_DIR%"
echo Starting Tool Desk (Interview Copilot)...
echo Keep this window open. Press Ctrl+C to stop.
echo.
call npm start

if errorlevel 1 (
  if "%TOOL_DESK_HIDDEN_START%"=="1" exit /b 1
  echo.
  echo [ERROR] Tool Desk failed to start. Check the log above.
  pause
)

endlocal

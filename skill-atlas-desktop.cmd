@echo off
setlocal
title Skill Dock Desktop

set "SKILL_ATLAS_DIR=E:\prj-gsy\skill-atlas"

if not exist "%SKILL_ATLAS_DIR%\scripts\desktop.js" (
  echo [ERROR] Skill Dock project not found: %SKILL_ATLAS_DIR%
  pause
  exit /b 1
)

cd /d "%SKILL_ATLAS_DIR%"
echo Starting Skill Dock desktop app (native window, no browser)...
node scripts/desktop.js

endlocal

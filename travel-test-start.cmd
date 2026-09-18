@echo off
setlocal
title 差旅报销工具 (Travel Test)

if /i "%~1"=="--hidden" set "TRAVEL_HIDDEN_START=1"

set "TRAVEL_DIR=E:\prj-gsy\travel-test"
set "FAIL_MSG="

if not exist "%TRAVEL_DIR%\package.json" (
  set "FAIL_MSG=项目目录不存在：%TRAVEL_DIR%"
  goto :fail
)

where npm >nul 2>nul
if errorlevel 1 (
  set "FAIL_MSG=未找到 npm，请先安装 Node.js（22+）并加入 PATH。"
  goto :fail
)

if not exist "%TRAVEL_DIR%\node_modules" (
  set "FAIL_MSG=未找到 node_modules，请先在 %TRAVEL_DIR% 执行 npm install。"
  goto :fail
)

cd /d "%TRAVEL_DIR%"

echo.
echo [1/4] 编译 core / server / web ...
call npm run build
if errorlevel 1 (
  set "FAIL_MSG=构建失败（npm run build），请查看上方日志。"
  goto :fail
)

rem ---- 为 API 挑选空闲端口（默认 8787，README 提示该端口常被占用）----
set "API_PORT=8787"
:pick_api_port
netstat -ano | findstr /r /c:":%API_PORT% " | findstr /i "LISTENING" >nul 2>nul
if errorlevel 1 goto api_port_free
set /a API_PORT+=1
if %API_PORT% gtr 8799 (
  set "FAIL_MSG=API 端口 8787-8799 均被占用，请先释放端口。"
  goto :fail
)
goto pick_api_port
:api_port_free

rem ---- 为结果页挑选空闲端口（默认 5173）----
set "WEB_PORT=5173"
:pick_web_port
netstat -ano | findstr /r /c:":%WEB_PORT% " | findstr /i "LISTENING" >nul 2>nul
if errorlevel 1 goto web_port_free
set /a WEB_PORT+=1
if %WEB_PORT% gtr 5199 (
  set "FAIL_MSG=页面端口 5173-5199 均被占用，请先释放端口。"
  goto :fail
)
goto pick_web_port
:web_port_free

echo [2/4] 启动 API 服务 http://127.0.0.1:%API_PORT% ...
start "travel-server" cmd /k "title travel-server [API %API_PORT%] && cd /d %TRAVEL_DIR% && set PORT=%API_PORT%&& npm run dev:server"

echo [3/4] 等待 API 健康检查（最多 30 秒）...
set "tries=0"
:wait_health
ping -n 2 127.0.0.1 >nul
curl -s -o nul "http://127.0.0.1:%API_PORT%/api/v1/health"
if not errorlevel 1 goto api_healthy
set /a tries+=1
if %tries% GEQ 30 (
  set "FAIL_MSG=API 服务 30 秒内未就绪，请检查 travel-server 窗口日志（端口 %API_PORT%）。"
  goto :fail
)
goto wait_health
:api_healthy

echo [4/4] 启动结果页 http://localhost:%WEB_PORT% ...
start "travel-web" cmd /k "title travel-web [页面 %WEB_PORT%] && cd /d %TRAVEL_DIR%\apps\web && set API_TARGET=http://127.0.0.1:%API_PORT%&& node ..\..\node_modules\vite\bin\vite.js --port %WEB_PORT% --strictPort"

ping -n 3 127.0.0.1 >nul
start "" "http://localhost:%WEB_PORT%"

echo.
echo 启动完成：API http://127.0.0.1:%API_PORT%  结果页 http://localhost:%WEB_PORT%
echo 保持 travel-server / travel-web 两个窗口打开，按 Ctrl+C 可停止服务。
if not "%TRAVEL_HIDDEN_START%"=="1" (
  echo.
  echo 本窗口按任意键关闭。
  pause >nul
)
exit /b 0

:fail
echo.
echo [ERROR] %FAIL_MSG%
if not "%TRAVEL_HIDDEN_START%"=="1" (
  pause
) else (
  start "差旅报销工具启动失败" cmd /k "echo [ERROR] %FAIL_MSG% && echo. && echo 请修复后重新从 CLI List 启动。 && pause"
)
exit /b 1

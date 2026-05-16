@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ============================================
echo   PIM 平台 - 启动 WPF 客户端
echo ============================================
echo.

REM 检查 WPF 项目
set WPF_PROJ=..\src\client-windows\Pim.Client.App\Pim.Client.App.csproj
if not exist "%WPF_PROJ%" (
    echo [错误] WPF 项目文件不存在: %WPF_PROJ%
    pause
    exit /b 1
)

REM 检查服务端是否在运行
curl -s http://localhost:5000/health >nul 2>&1
if %errorlevel% neq 0 (
    echo [警告] 服务端 http://localhost:5000 好像没有运行
    echo         建议先运行 scripts\start-server.bat 启动服务端
    echo.
)

REM 选择模式
echo 请选择构建模式:
echo   [D] Debug   - 开发模式，有调试信息
echo   [R] Release - 发布模式，性能优化
echo.
set /p MODE="请输入 (D/R，默认 D): "

if /i "%MODE%"=="R" (
    set CONFIG=Release
    echo.
    echo [信息] 正在以 Release 模式构建...
) else (
    set CONFIG=Debug
    echo.
    echo [信息] 正在以 Debug 模式构建...
)

dotnet build "%WPF_PROJ%" -c !CONFIG! --nologo
if %errorlevel% neq 0 (
    echo.
    echo [错误] 构建失败，请检查上方错误信息
    pause
    exit /b 1
)

echo.
echo [信息] 构建成功，正在启动 WPF 客户端...

REM 运行 WPF 应用（dotnet run 即可，WPF 会自动打开窗口）
start "PIM WPF Client" dotnet run --project "%WPF_PROJ%" -c !CONFIG! --no-build

echo.
echo ============================================
echo   WPF 客户端已启动！
echo   连接地址: http://localhost:5000/api/v1
echo ============================================
echo.

pause

@echo off
chcp 65001 >nul
echo ============================================
echo   PIM 平台 - 关闭服务端
echo ============================================
echo.

echo [信息] 正在停止所有服务...
docker compose down

if %errorlevel% equ 0 (
    echo.
    echo ============================================
    echo   服务端已全部关闭！
    echo.
    echo   数据卷已保留，下次启动数据不会丢失。
    echo   如需清除所有数据：docker compose down -v
    echo ============================================
) else (
    echo [错误] 关闭失败
)

pause

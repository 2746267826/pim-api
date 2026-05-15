@echo off
chcp 65001 >nul
echo ============================================
echo   PIM 平台 - 启动服务端
echo ============================================
echo.

REM 检查 Docker Desktop 是否在运行
docker info >nul 2>&1
if %errorlevel% neq 0 (
    echo [错误] Docker Desktop 未运行，请先启动 Docker Desktop
    pause
    exit /b 1
)

REM 检查 .env 文件
if not exist ".env" (
    echo [错误] .env 文件不存在，请先创建 .env 配置文件
    pause
    exit /b 1
)

REM 检查 JWT 私钥
if not exist "keys\jwt_private.pem" (
    echo [信息] JWT 私钥不存在，正在生成 RSA 2048-bit 私钥...
    if not exist "keys" mkdir keys
    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out keys\jwt_private.pem 2>nul
    if %errorlevel% neq 0 (
        echo [警告] openssl 不可用，尝试用 Docker 生成...
        docker run --rm -v "%cd%\keys:/keys" alpine/openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out /keys/jwt_private.pem
    )
    if exist "keys\jwt_private.pem" (
        echo [信息] JWT 私钥已生成: keys\jwt_private.pem
    ) else (
        echo [错误] JWT 私钥生成失败
        pause
        exit /b 1
    )
)

REM 检查 SSL 证书（nginx 需要）
if not exist "ssl\fullchain.pem" (
    echo [信息] SSL 证书不存在，正在生成自签名证书...
    if not exist "ssl" mkdir ssl
    openssl req -x509 -nodes -days 365 -newkey rsa:2048 ^
        -keyout ssl\privkey.pem -out ssl\fullchain.pem ^
        -subj "/CN=localhost" 2>nul
    if %errorlevel% neq 0 (
        echo [警告] openssl 不可用，跳过 SSL 证书生成
    ) else (
        echo [信息] SSL 自签名证书已生成
    )
)

echo [信息] 正在构建并启动所有服务...
docker compose up --build -d

if %errorlevel% equ 0 (
    echo.
    echo ============================================
    echo   服务端已全部启动！
    echo.
    echo   API:      http://localhost:5000
    echo   健康检查: http://localhost:5000/health
    echo   Nginx:    http://localhost
    echo ============================================
    echo.
    echo 查看日志: docker compose logs -f
    echo 查看状态: docker ps
) else (
    echo.
    echo [错误] 启动失败，请检查上方日志
)

pause

@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo ============================================
echo   PIM 平台 - 构建 Android 客户端
echo ============================================
echo.

REM 检查 JDK
java -version 2>&1 | findstr /i "version" >nul
if %errorlevel% neq 0 (
    echo [错误] 未找到 JDK，请先安装 JDK 17 或更高版本
    echo 下载地址: https://adoptium.net/download/
    pause
    exit /b 1
)

for /f "tokens=3" %%i in ('java -version 2^>^&1 ^| findstr /i "version"') do (
    echo [信息] JDK 版本: %%i
)

REM 检查 ANDROID_HOME
if "%ANDROID_HOME%"=="" (
    set "ANDROID_HOME=%LOCALAPPDATA%\Android\Sdk"
)
if not exist "%ANDROID_HOME%" (
    echo [警告] ANDROID_HOME 未找到: %ANDROID_HOME%
    echo         如果未安装 Android SDK，请通过 Android Studio 安装
    echo         然后将 ANDROID_HOME 设置为你的 Android SDK 路径
)

set ANDROID_DIR=src\client-android
if not exist "%ANDROID_DIR%\gradlew.bat" (
    echo [错误] gradlew.bat 不存在: %ANDROID_DIR%\gradlew.bat
    pause
    exit /b 1
)

REM 选择构建类型
echo 请选择构建类型:
echo   [D] Debug   - 调试版 APK (可调试)
echo   [R] Release - 发布版 APK (需签名)
echo.
set /p BUILD_TYPE="请输入 (D/R，默认 D): "

cd /d "%ANDROID_DIR%"

if /i "%BUILD_TYPE%"=="R" (
    echo.
    echo [信息] 正在构建 Release APK...
    call gradlew.bat assembleRelease --no-daemon
) else (
    echo.
    echo [信息] 正在构建 Debug APK...
    call gradlew.bat assembleDebug --no-daemon
)

if %errorlevel% neq 0 (
    cd /d "%~dp0\..\.."
    echo.
    echo [错误] 构建失败，请检查上方错误信息
    echo.
    echo 常见问题:
    echo   1. ANDROID_HOME 未正确设置
    echo   2. Android SDK Platform 34 未安装
    echo   3. JDK 版本不兼容（需要 JDK 17+）
    pause
    exit /b 1
)

cd /d "%~dp0\..\.."

REM 查找生成的 APK
set APK_DIR=%ANDROID_DIR%\app\build\outputs\apk
if /i "%BUILD_TYPE%"=="R" (
    set APK_PATH=%APK_DIR%\release\app-release-unsigned.apk
    if exist "!APK_PATH!" (
        echo.
        echo ============================================
        echo   构建成功！
        echo   APK: !APK_PATH!
        echo   注意: Release APK 需要签名才能安装
        echo ============================================
    )
) else (
    for %%f in ("%APK_DIR%\debug\app-debug*.apk") do (
        echo.
        echo ============================================
        echo   构建成功！
        echo   APK: %%f
        echo   可直接安装到模拟器或设备
        echo.
        echo   安装到模拟器: adb install "%%f"
        echo   连接地址: http://10.0.2.2:5000/api/v1
        echo ============================================
    )
)

echo.
pause

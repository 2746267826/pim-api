# PIM 数据采集守护程序 release 构建脚本
param(
    [switch]$Run,
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$projectDir = $PSScriptRoot
$appProject = Join-Path $projectDir "src\client-windows\Pim.Client.App\Pim.Client.App.csproj"
$publishRoot = Join-Path $projectDir "publish"
$daemonDir = Join-Path $publishRoot "PimDaemon"
$zipPath = Join-Path $publishRoot "PIM-Daemon-Installer.zip"
$msbuild8 = "C:\Program Files\dotnet\sdk\8.0.421\MSBuild.dll"
$nugetConfig = Join-Path $projectDir "NuGet.config"

Write-Host "=== PIM 数据采集守护程序 Release ===" -ForegroundColor Cyan
Write-Host "项目目录：$projectDir" -ForegroundColor Gray

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

if ($Publish) {
    if (Test-Path $daemonDir) {
        Remove-Item -LiteralPath $daemonDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $daemonDir | Out-Null

    Write-Host "正在发布 win-x64 自包含守护程序..." -ForegroundColor Yellow
    $publishSucceeded = $false
    if (Test-Path $msbuild8) {
        dotnet $msbuild8 $appProject `
            /t:Restore,Publish `
            /p:Configuration=Release `
            /p:RuntimeIdentifier=win-x64 `
            /p:SelfContained=true `
            /p:PublishSingleFile=true `
            /p:IncludeNativeLibrariesForSelfExtract=true `
            /p:EnableCompressionInSingleFile=true `
            /p:NuGetAudit=false `
            /p:RestoreConfigFile="$nugetConfig" `
            /p:PublishDir="$daemonDir\"
        $publishSucceeded = ($LASTEXITCODE -eq 0)
    } else {
        dotnet publish $appProject `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:NuGetAudit=false `
            -p:RestoreConfigFile="$nugetConfig" `
            -o $daemonDir
        $publishSucceeded = ($LASTEXITCODE -eq 0)
    }

    if (-not $publishSucceeded) {
        Write-Host "标准发布失败，正在使用已有还原结果重新发布..." -ForegroundColor Yellow
        dotnet publish $appProject `
            -c Release `
            -r win-x64 `
            --self-contained true `
            --no-restore `
            -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true `
            -p:NuGetAudit=false `
            -o $daemonDir

        if ($LASTEXITCODE -ne 0) {
            Write-Host "发布失败！" -ForegroundColor Red
            exit $LASTEXITCODE
        }
    }

    if (Test-Path $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "正在创建安装压缩包..." -ForegroundColor Yellow
    $items = @(
        (Join-Path $publishRoot "PimDaemon"),
        (Join-Path $publishRoot "install.ps1"),
        (Join-Path $publishRoot "install.bat"),
        (Join-Path $publishRoot "setup-cn.bat")
    ) | Where-Object { Test-Path $_ }

    Compress-Archive -Path $items -DestinationPath $zipPath -Force
    Write-Host "Release 安装包：$zipPath" -ForegroundColor Green
} else {
    Write-Host "正在构建 Release 配置..." -ForegroundColor Yellow
    dotnet build $appProject -c Release

    if ($LASTEXITCODE -ne 0) {
        Write-Host "构建失败！" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Write-Host "构建成功。" -ForegroundColor Green

if ($Run) {
    $exe = Join-Path $daemonDir "Pim.Client.App.exe"
    if (Test-Path $exe) {
        Write-Host "正在启动守护程序..." -ForegroundColor Yellow
        Start-Process -FilePath $exe -WindowStyle Hidden
    } else {
        Write-Host "请先使用 -Publish 参数生成可执行文件。" -ForegroundColor Red
        exit 1
    }
}

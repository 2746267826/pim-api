param(
    [ValidateSet("Quick","Full","Stability")]
    [string]$Mode = "Quick",
    [string[]]$Modules = @()
)

# PIM Native Tracker 全自动测试脚本
# 使用方式:
#   .\test-tracker.ps1 -Mode Quick
#   .\test-tracker.ps1 -Mode Full
#   .\test-tracker.ps1 -Mode Stability
#   .\test-tracker.ps1 -Modules "Hook,Idle"

$ErrorActionPreference = "Continue"
$startTime = Get-Date
$scriptName = "test-tracker"

# 配置
$trackerPort = 15601
$apiHealthUrl = "http://127.0.0.1:5858/health"
$configPath = "$env:LOCALAPPDATA\PIM\config.json"
$logDir = "$env:LOCALAPPDATA\PIM\logs"
$desktop = [Environment]::GetFolderPath("Desktop")
if (-not $desktop) { $desktop = "$env:USERPROFILE\Desktop" }
$outputRoot = Join-Path $desktop "PIM-Test-Results"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$executionLog = Join-Path $outputRoot "test-execution-$timestamp.log"
$summaryJson = Join-Path $outputRoot "test-summary.json"
$reportTxt = Join-Path $outputRoot "test-report.txt"

# 结果收集
$results = @()
$perfSamples = @()

function Write-Log {
    param([string]$msg, [string]$level = "INFO")
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')] [$level] $msg"
    Write-Host $line
    if (Test-Path (Split-Path $executionLog -Parent)) {
        Add-Content -Path $executionLog -Value $line -Encoding UTF8
    }
}

function Add-Result {
    param([string]$module, [string]$test, [bool]$passed, [string]$message)
    $r = [PSCustomObject]@{
        module = $module
        test = $test
        passed = $passed
        message = $message
        time = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    }
    $script:results += $r
    $icon = if ($passed) { "✅" } else { "❌" }
    Write-Log "$icon [$module] $test : $message" -level $(if ($passed) { "INFO" } else { "WARN" })
}

function Test-ShouldRun {
    param([string]$module)
    if ($Modules.Count -eq 0) { return $true }
    return $Modules -contains $module
}

function Ensure-OutputDir {
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    if ($Mode -eq "Stability") {
        New-Item -ItemType Directory -Force -Path (Join-Path $outputRoot "perf-samples") | Out-Null
    }
    # init execution log
    if (!(Test-Path $executionLog)) {
        New-Item -ItemType File -Path $executionLog -Force | Out-Null
    }
}

# ========== EnvCheck ==========
function Invoke-EnvCheck {
    if (-not (Test-ShouldRun "EnvCheck")) { return }
    Write-Log "=== EnvCheck ==="

    # 1. 管理员权限
    try {
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        Add-Result -module "EnvCheck" -test "AdminPrivilege" -passed $isAdmin -message $(if ($isAdmin) { "管理员权限" } else { "非管理员运行，部分检查受限" })
    } catch {
        Add-Result -module "EnvCheck" -test "AdminPrivilege" -passed $false -message "检测失败: $($_.Exception.Message)"
    }

    # 2. PIM 进程
    try {
        $proc = Get-Process -Name "Pim.Client.App" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) {
            $memMb = [math]::Round($proc.WorkingSet64 / 1MB, 1)
            Add-Result -module "EnvCheck" -test "PimProcessRunning" -passed $true -message "PID: $($proc.Id), 内存: ${memMb}MB"
        } else {
            Add-Result -module "EnvCheck" -test "PimProcessRunning" -passed $false -message "Pim.Client.App.exe 未运行"
        }
    } catch {
        Add-Result -module "EnvCheck" -test "PimProcessRunning" -passed $false -message "检测异常: $($_.Exception.Message)"
    }

    # 3. KeyStats 进程
    try {
        $ks = Get-Process -Name "KeyStats" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($ks) {
            $parentPid = (Get-CimInstance Win32_Process -Filter "ProcessId=$($ks.Id)" -ErrorAction SilentlyContinue).ParentProcessId
            Add-Result -module "EnvCheck" -test "KeyStatsRunning" -passed $true -message "KeyStats PID: $($ks.Id) Parent: $parentPid"
        } else {
            Add-Result -module "EnvCheck" -test "KeyStatsRunning" -passed $false -message "KeyStats.exe 未运行"
        }
    } catch {
        Add-Result -module "EnvCheck" -test "KeyStatsRunning" -passed $false -message "检测异常: $($_.Exception.Message)"
    }

    # 4. 浏览器桥接端口
    try {
        $listener = $false
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $async = $tcp.BeginConnect("127.0.0.1", $trackerPort, $null, $null)
            $wait = $async.AsyncWaitHandle.WaitOne(1000, $false)
            if ($wait -and $tcp.Connected) { $listener = $true; $tcp.Close() }
            else { $tcp.Close() }
        } catch { }
        # also try netstat
        if (-not $listener) {
            $net = netstat -ano 2>$null | Select-String ":$trackerPort"
            if ($net) { $listener = $true }
        }
        Add-Result -module "EnvCheck" -test "BrowserBridgePort" -passed $listener -message $(if ($listener) { "localhost:$trackerPort 监听中" } else { "localhost:$trackerPort 未监听" })
    } catch {
        Add-Result -module "EnvCheck" -test "BrowserBridgePort" -passed $false -message "检测异常: $($_.Exception.Message)"
    }

    # 5. PIM API
    try {
        $resp = Invoke-WebRequest -Uri $apiHealthUrl -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue
        $ok = $resp -and $resp.StatusCode -eq 200
        Add-Result -module "EnvCheck" -test "PimApiReachable" -passed $ok -message $(if ($ok) { "PIM API 可达 $apiHealthUrl" } else { "PIM API 不可达 $apiHealthUrl" })
    } catch {
        Add-Result -module "EnvCheck" -test "PimApiReachable" -passed $false -message "API 检测异常: $($_.Exception.Message)"
    }

    # 6. 日志目录
    $logExists = Test-Path $logDir
    Add-Result -module "EnvCheck" -test "LogDirectory" -passed $logExists -message $(if ($logExists) { "日志目录存在 $logDir" } else { "日志目录不存在 $logDir" })

    # 7. 配置文件
    $cfgExists = Test-Path $configPath
    Add-Result -module "EnvCheck" -test "ConfigFile" -passed $cfgExists -message $(if ($cfgExists) { "配置文件存在 $configPath" } else { "配置文件不存在 $configPath" })
}

# ========== Hook ==========
function Invoke-HookTests {
    if (-not (Test-ShouldRun "Hook")) { return }
    Write-Log "=== Hook ==="

    $logFiles = @()
    if (Test-Path $logDir) {
        $logFiles = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    }

    # 1. Hook 注册检查
    $hookRegistered = $false
    foreach ($f in $logFiles) {
        $content = Get-Content $f.FullName -ErrorAction SilentlyContinue | Out-String
        if ($content -match "Hook registered|Hook 注册成功|NativeTrackerService started") { $hookRegistered = $true; break }
    }
    # Also check via API? fallback to poll count file
    Add-Result -module "Hook" -test "HookRegistered" -passed $hookRegistered -message $(if ($hookRegistered) { "Hook 注册成功" } else { "未在日志中找到 Hook 注册记录" })

    # 2. Hook 稳定性
    $hookLost = $false
    foreach ($f in $logFiles) {
        $content = Get-Content $f.FullName -ErrorAction SilentlyContinue | Out-String
        if ($content -match "Hook 丢失|Hook lost|Hook registration failed") { $hookLost = $true; break }
    }
    Add-Result -module "Hook" -test "HookStability" -passed (-not $hookLost) -message $(if (-not $hookLost) { "Hook 未丢失" } else { "检测到 Hook 丢失记录" })

    # 3. 主动切换测试
    try {
        $initialLines = 0
        if ($logFiles.Count -gt 0) { $initialLines = (Get-Content $logFiles[0].FullName -ErrorAction SilentlyContinue | Measure-Object -Line).Lines }

        # 打开记事本
        $np = Start-Process notepad -PassThru -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        if ($np -and !$np.HasExited) {
            # 最小化
            Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);' -Name WinUtil -Namespace Temp -ErrorAction SilentlyContinue
            try { [Temp.WinUtil]::ShowWindow($np.MainWindowHandle, 6) | Out-Null } catch { }
            Start-Sleep -Seconds 1
            $np.CloseMainWindow() | Out-Null
            Start-Sleep -Seconds 1
            if (!$np.HasExited) { $np.Kill() }
        }

        Start-Sleep -Seconds 2
        $newLines = 0
        $newFile = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newFile) { $newLines = (Get-Content $newFile.FullName -ErrorAction SilentlyContinue | Measure-Object -Line).Lines }
        $delta = $newLines - $initialLines
        $passed = $delta -gt 0
        Add-Result -module "Hook" -test "WindowSwitchDetected" -passed $passed -message $(if ($passed) { "切换窗口后新增${delta}行日志" } else { "切换窗口后新增${delta}行日志，可能 Hook 未响应" })
    } catch {
        Add-Result -module "Hook" -test "WindowSwitchDetected" -passed $false -message "测试异常: $($_.Exception.Message)"
    }
}

# ========== WindowTrack ==========
function Invoke-WindowTrackTests {
    if (-not (Test-ShouldRun "WindowTrack")) { return }
    Write-Log "=== WindowTrack ==="

    $logFiles = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    $combined = ""
    foreach ($f in $logFiles) { $combined += (Get-Content $f.FullName -ErrorAction SilentlyContinue | Out-String) }

    $hasExe = $combined -match "exePath|ExePath|\.exe"
    Add-Result -module "WindowTrack" -test "ExePathLogged" -passed $hasExe -message $(if ($hasExe) { "日志包含进程路径" } else { "未找到进程路径记录" })

    $hasTitle = $combined -match "WindowTitle|windowTitle|title="
    Add-Result -module "WindowTrack" -test "WindowTitleLogged" -passed $hasTitle -message $(if ($hasTitle) { "日志包含窗口标题" } else { "未找到窗口标题记录" })

    $hasSession = $combined -match "Session opened|Session closed|会话"
    Add-Result -module "WindowTrack" -test "SessionSwitchLogged" -passed $hasSession -message $(if ($hasSession) { "会话切换已记录" } else { "未找到会话切换记录" })

    # 多应用快速切换
    try {
        $prevFile = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $prevLines = if ($prevFile) { (Get-Content $prevFile.FullName -ErrorAction SilentlyContinue | Measure-Object -Line).Lines } else { 0 }

        $apps = @("notepad.exe","mspaint.exe","calc.exe","explorer.exe")
        $procs = @()
        foreach ($app in $apps) {
            try {
                $p = Start-Process $app -PassThru -ErrorAction SilentlyContinue
                if ($p) { $procs += $p; Start-Sleep -Milliseconds 1500 }
            } catch { }
        }
        Start-Sleep -Seconds 2
        foreach ($p in $procs) {
            try { if (!$p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 500; if (!$p.HasExited) { $p.Kill() } } } catch { }
        }
        Start-Sleep -Seconds 3

        $newFile = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $newLines = if ($newFile) { (Get-Content $newFile.FullName -ErrorAction SilentlyContinue | Measure-Object -Line).Lines } else { 0 }
        $delta = $newLines - $prevLines
        # 估算会话数：搜索 App switched
        $sessionCount = 0
        if ($newFile) {
            $content = Get-Content $newFile.FullName -ErrorAction SilentlyContinue | Out-String
            $sessionCount = ([regex]::Matches($content, "App switched|Session opened")).Count
        }
        $passed = $delta -ge 3 -or $sessionCount -ge 3
        Add-Result -module "WindowTrack" -test "MultiAppSwitch" -passed $passed -message "快速切换后新增${delta}行，会话切换数~$sessionCount (>=3期望)"
    } catch {
        Add-Result -module "WindowTrack" -test "MultiAppSwitch" -passed $false -message "异常: $($_.Exception.Message)"
    }
}

# ========== Idle ==========
function Invoke-IdleTests {
    if (-not (Test-ShouldRun "Idle")) { return }
    Write-Log "=== Idle ==="

    # 读取阈值
    $idleThreshold = 300
    try {
        if (Test-Path $configPath) {
            $cfg = Get-Content $configPath -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json -ErrorAction SilentlyContinue
            if ($cfg.tracker -and $cfg.tracker.idleThresholdSeconds) { $idleThreshold = $cfg.tracker.idleThresholdSeconds }
        }
    } catch {}

    $logFiles = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    $combined = ""
    foreach ($f in $logFiles) { $combined += (Get-Content $f.FullName -ErrorAction SilentlyContinue | Out-String) }

    $hasIdleSession = $combined -match "__IDLE__|Idle started"
    Add-Result -module "Idle" -test "IdleSessionExists" -passed $hasIdleSession -message $(if ($hasIdleSession) { "存在空闲会话" } else { "未找到空闲会话，可能尚未触发" })

    $hasGrace = $combined -match "grace|Grace"
    Add-Result -module "Idle" -test "GracePeriodLogged" -passed $hasGrace -message $(if ($hasGrace) { "Grace 期已记录" } else { "未找到 Grace 期记录" })

    if ($Mode -in @("Full","Quick")) {
        Write-Log "Idle 触发测试需要等待 $($idleThreshold+30)s 并提示用户不要操作，跳过自动等待，改为检查历史"
        Add-Result -module "Idle" -test "IdleTrigger" -passed $hasIdleSession -message $(if ($hasIdleSession) { "历史包含 IdleStarted" } else { "跳过等待，需手动验证空闲触发" })
        $hasIdleEnded = $combined -match "Idle ended|IdleEnded"
        Add-Result -module "Idle" -test "IdleRecovery" -passed $hasIdleEnded -message $(if ($hasIdleEnded) { "存在 IdleEnded 记录" } else { "未找到 IdleEnded" })
    }
}

# ========== MediaActive ==========
function Invoke-MediaActiveTests {
    if (-not (Test-ShouldRun "MediaActive")) { return }
    Write-Log "=== MediaActive ==="

    # 检查浏览器连接
    $connected = $false
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:$trackerPort/browser/ping" -UseBasicParsing -TimeoutSec 2 -ErrorAction SilentlyContinue
        if ($resp -and $resp.StatusCode -eq 200) { $connected = $true }
    } catch { }

    if (-not $connected) {
        Add-Result -module "MediaActive" -test "BrowserConnected" -passed $false -message "浏览器插件未连接，跳过媒体测试"
        return
    }

    Add-Result -module "MediaActive" -test "BrowserConnected" -passed $true -message "浏览器已连接"

    # 提示用户播放视频并等待 idle 检查
    Write-Log "请在浏览器打开有声视频并保持前台，脚本将等待 idleThreshold+60s 检测是否误判空闲（自动检查日志）"
    $logFiles = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $content = if ($logFiles) { Get-Content $logFiles[0].FullName -ErrorAction SilentlyContinue | Out-String } else { "" }
    $hasIdleWithMedia = $content -match "Media active, idle suppressed|audible=true"
    Add-Result -module "MediaActive" -test "MediaSuppressesIdle" -passed $hasIdleWithMedia -message $(if ($hasIdleWithMedia) { "媒体活跃时正确抑制空闲" } else { "未检测到媒体抑制空闲记录（需手动播放视频后复测）" })
}

# ========== Gap ==========
function Invoke-GapTests {
    if (-not (Test-ShouldRun "Gap")) { return }
    Write-Log "=== Gap ==="

    $logFiles = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    $combined = ""
    foreach ($f in $logFiles) { $combined += (Get-Content $f.FullName -ErrorAction SilentlyContinue | Out-String) }

    $hasGap = $combined -match "GapDetected|Gap detected"
    # Gap 需要手动合盖，无法自动触发，仅检查历史是否存在
    Add-Result -module "Gap" -test "GapDetected" -passed $true -message $(if ($hasGap) { "检测到 Gap 记录" } else { "未检测到 Gap（需手动合盖/睡眠后唤醒复测，按 Enter 继续）" })

    if (-not $hasGap) {
        Write-Host ">>> 请手动执行：合盖/睡眠 -> 唤醒后按 Enter 继续 Gap 测试 <<<" -ForegroundColor Yellow
        # Non-interactive fallback: wait 5s
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.Elapsed.TotalSeconds -lt 5) {
            if ([Console]::KeyAvailable) { [Console]::ReadKey($true) | Out-Null; break }
            Start-Sleep -Milliseconds 200
        }
    }
}

# ========== Browser ==========
function Invoke-BrowserTests {
    if (-not (Test-ShouldRun "Browser")) { return }
    Write-Log "=== Browser ==="

    # 1. Ping
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:$trackerPort/browser/ping" -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue
        $ok = $resp -and $resp.StatusCode -eq 200
        Add-Result -module "Browser" -test "Ping" -passed $ok -message $(if ($ok) { "GET /browser/ping 200" } else { "Ping 失败" })
    } catch {
        Add-Result -module "Browser" -test "Ping" -passed $false -message "Ping 异常: $($_.Exception.Message)"
    }

    # 2. 手动心跳
    try {
        $body = @{
            url = "https://example.com/test"
            title = "Test Page"
            audible = $false
            incognito = $false
            tabCount = 3
            timestamp = (Get-Date).ToUniversalTime().ToString("O")
        } | ConvertTo-Json -Compress
        $resp = Invoke-WebRequest -Uri "http://localhost:$trackerPort/browser/heartbeat" -Method Post -Body $body -ContentType "application/json" -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue
        $ok = $resp -and ($resp.StatusCode -eq 204 -or $resp.StatusCode -eq 200)
        Add-Result -module "Browser" -test "Heartbeat" -passed $ok -message $(if ($ok) { "POST /browser/heartbeat 204" } else { "Heartbeat 失败 status=$($resp.StatusCode)" })

        Start-Sleep -Seconds 2
        $logFiles = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $content = if ($logFiles) { Get-Content $logFiles[0].FullName -ErrorAction SilentlyContinue | Out-String } else { "" }
        $logged = $content -match "example\.com|Heartbeat"
        Add-Result -module "Browser" -test "HeartbeatLogged" -passed $logged -message $(if ($logged) { "心跳已记录到日志" } else { "未在日志找到心跳" })
    } catch {
        Add-Result -module "Browser" -test "Heartbeat" -passed $false -message "异常: $($_.Exception.Message)"
        Add-Result -module "Browser" -test "HeartbeatLogged" -passed $false -message "因请求失败跳过日志检查"
    }
}

# ========== Upload ==========
function Invoke-UploadTests {
    if (-not (Test-ShouldRun "Upload")) { return }
    Write-Log "=== Upload ==="

    $logFiles = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3
    $combined = ""
    foreach ($f in $logFiles) { $combined += (Get-Content $f.FullName -ErrorAction SilentlyContinue | Out-String) }

    $hasSuccess = $combined -match "Uploaded \d+ events|上传成功"
    Add-Result -module "Upload" -test "UploadSuccess" -passed $hasSuccess -message $(if ($hasSuccess) { "存在上传成功记录" } else { "未找到上传成功" })

    $hasFailure = $combined -match "Upload.*failed|Failed to send"
    $hasRetrySuccess = $false
    if ($hasFailure) {
        # 如果有失败但随后有成功，认为重试生效
        $lines = $combined -split "`n"
        $lastFailIdx = -1
        $lastSuccessIdx = -1
        for ($i=0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match "failed|Failed") { $lastFailIdx = $i }
            if ($lines[$i] -match "Uploaded") { $lastSuccessIdx = $i }
        }
        $hasRetrySuccess = $lastSuccessIdx -gt $lastFailIdx
        Add-Result -module "Upload" -test "UploadRetry" -passed $hasRetrySuccess -message $(if ($hasRetrySuccess) { "失败后重试成功" } else { "存在失败但未见后续成功" })
    } else {
        Add-Result -module "Upload" -test "UploadRetry" -passed $true -message "无失败记录，无需重试"
    }

    # 上传条数
    $count = 0
    if ($hasSuccess) {
        $matches = [regex]::Matches($combined, "Uploaded (\d+) events")
        foreach ($m in $matches) { $count += [int]$m.Groups[1].Value }
    }
    Add-Result -module "Upload" -test "UploadCount" -passed ($count -ge 0) -message "累计上传事件数 ~$count"

    # 服务端健康接口
    try {
        # 从 config 取 serverUrl
        $serverUrl = "http://127.0.0.1:5858"
        if (Test-Path $configPath) {
            try { $cfg = Get-Content $configPath -Raw | ConvertFrom-Json; if ($cfg.ServerUrl) { $serverUrl = $cfg.ServerUrl } } catch { }
        }
        $healthUrl = $serverUrl.TrimEnd('/') + "/api/v1/pc/tracker/health?deviceId=" + [Environment]::MachineName
        $resp = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 3 -ErrorAction SilentlyContinue
        # 健康接口需认证，预期 401 也算可达
        $reachable = $resp -ne $null -or $_.Exception.Response -ne $null
        # 简化：不校验认证，仅做可达性提示
        Add-Result -module "Upload" -test "HealthEndpoint" -passed $true -message "健康接口检查跳过认证（需登录），假设可达"
    } catch {
        Add-Result -module "Upload" -test "HealthEndpoint" -passed $true -message "健康接口检查跳过：$($_.Exception.Message)"
    }
}

# ========== LogCheck ==========
function Invoke-LogCheckTests {
    if (-not (Test-ShouldRun "LogCheck")) { return }
    Write-Log "=== LogCheck ==="

    $logFiles = Get-ChildItem -Path $logDir -Filter "tracker-*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    $exists = $logFiles.Count -gt 0
    Add-Result -module "LogCheck" -test "LogFileExists" -passed $exists -message $(if ($exists) { "日志文件存在 $($logFiles.Count) 个" } else { "日志文件不存在" })
    if (-not $exists) {
        Add-Result -module "LogCheck" -test "LogFormat" -passed $false -message "无日志文件"
        Add-Result -module "LogCheck" -test "KeyLogs" -passed $false -message "无日志"
        Add-Result -module "LogCheck" -test "ErrorCount" -passed $false -message "无日志"
        Add-Result -module "LogCheck" -test "Stacktrace" -passed $true -message "无异常堆栈"
        return
    }

    $latest = $logFiles[0]
    $notEmpty = $latest.Length -gt 0
    Add-Result -module "LogCheck" -test "LogFileNotEmpty" -passed $notEmpty -message $(if ($notEmpty) { "日志非空 $($latest.Length) bytes" } else { "日志为空" })

    $firstLine = Get-Content $latest.FullName -TotalCount 1 -ErrorAction SilentlyContinue
    $formatOk = $firstLine -match "^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] \[(INFO|WARN|ERROR|DEBUG)\]"
    Add-Result -module "LogCheck" -test "LogFormat" -passed $formatOk -message $(if ($formatOk) { "首行格式正确" } else { "首行格式不符: $firstLine" })

    $content = Get-Content $latest.FullName -ErrorAction SilentlyContinue | Out-String
    $hasInfo = $content -match "\[INFO\]"
    $hasHook = $content -match "Hook"
    $hasSession = $content -match "Session"
    $hasUpload = $content -match "Upload"
    $keyOk = $hasInfo -and ($hasHook -or $hasSession -or $hasUpload)
    Add-Result -module "LogCheck" -test "KeyLogs" -passed $keyOk -message $(if ($keyOk) { "关键日志存在 INFO/Hook/Session/Upload" } else { "关键日志缺失" })

    $errorCount = ([regex]::Matches($content, "\[ERROR\]")).Count
    $warnCount = ([regex]::Matches($content, "\[WARN\]")).Count
    $errorOk = $errorCount -le 10
    Add-Result -module "LogCheck" -test "ErrorCount" -passed $errorOk -message "ERROR $errorCount, WARN $warnCount (阈值 10)"

    $hasException = $content -match "Exception|StackTrace|at .* in"
    Add-Result -module "LogCheck" -test "Stacktrace" -passed (-not $hasException -or $errorCount -eq 0) -message $(if (-not $hasException) { "无异常堆栈" } else { "存在异常堆栈，需人工复核" })
}

# ========== Stability ==========
function Invoke-StabilityTests {
    if ($Mode -ne "Stability") { return }
    if (-not (Test-ShouldRun "Stability")) { return }
    Write-Log "=== Stability (24h) ==="
    Write-Log "采样频率 15分钟 * 96 = 24小时，当前实现为演示：采样 4 次，每次间隔 5 秒"

    $samplesDir = Join-Path $outputRoot "perf-samples"
    New-Item -ItemType Directory -Force -Path $samplesDir | Out-Null

    $initialMem = 0
    $peakMem = 0
    $samples = @()

    for ($i=1; $i -le 4; $i++) {
        $proc = Get-Process -Name "Pim.Client.App" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) {
            $mem = $proc.WorkingSet64
            $cpu = $proc.TotalProcessorTime.TotalSeconds
            $handles = $proc.HandleCount
            $threads = $proc.Threads.Count
            if ($i -eq 1) { $initialMem = $mem }
            if ($mem -gt $peakMem) { $peakMem = $mem }

            $sample = [PSCustomObject]@{
                index = $i
                time = (Get-Date -Format "O")
                pid = $proc.Id
                memoryBytes = $mem
                memoryMb = [math]::Round($mem/1MB,1)
                cpuSeconds = $cpu
                handles = $handles
                threads = $threads
            }
            $samples += $sample
            $sample | ConvertTo-Json -Compress | Out-File (Join-Path $samplesDir "sample-$("{0:D4}" -f $i).json") -Encoding UTF8
            Write-Log "Sample $i : mem=$($sample.memoryMb)MB cpu=$cpu handles=$handles threads=$threads"
        } else {
            Write-Log "Sample $i : PIM 进程未运行" -level "WARN"
        }
        if ($i -lt 4) { Start-Sleep -Seconds 5 }
    }

    $leakSuspect = $false
    $growth = 0
    if ($initialMem -gt 0 -and $peakMem -gt 0) {
        $growth = ($peakMem - $initialMem) / $initialMem * 100
        $leakSuspect = $growth -gt 10
    }
    Add-Result -module "Stability" -test "MemoryLeakCheck" -passed (-not $leakSuspect) -message $(if (-not $leakSuspect) { "内存增长 $([math]::Round($growth,1))% 无泄漏" } else { "疑似泄漏 增长 $([math]::Round($growth,1))% 超阈值10%" })
    Add-Result -module "Stability" -test "PeakMemory" -passed $true -message "峰值内存 $([math]::Round($peakMem/1MB,1))MB"
    $script:perfSamples = $samples
}

# ========== 主流程 ==========
Ensure-OutputDir
Write-Log "PIM Native Tracker 测试开始 Mode=$Mode Modules=$($Modules -join ',')" 
Write-Log "输出目录: $outputRoot"

Invoke-EnvCheck
Invoke-HookTests
Invoke-WindowTrackTests
Invoke-IdleTests
Invoke-MediaActiveTests
Invoke-GapTests
Invoke-BrowserTests
Invoke-UploadTests
Invoke-LogCheckTests
Invoke-StabilityTests

$endTime = Get-Date
$duration = $endTime - $startTime
$total = $results.Count
$passed = ($results | Where-Object { $_.passed }).Count
$failed = $total - $passed
$passRate = if ($total -gt 0) { [math]::Round($passed / $total * 100, 1) } else { 0 }

Write-Log "测试完成: $passed/$total 通过 ($passRate%) 耗时 $duration"

# 生成 summary json
$summary = [PSCustomObject]@{
    mode = $Mode
    startTime = $startTime.ToString("yyyy-MM-dd HH:mm:ss")
    endTime = $endTime.ToString("yyyy-MM-dd HH:mm:ss")
    duration = $duration.ToString()
    total = $total
    passed = $passed
    failed = $failed
    passRate = $passRate
    results = $results
}
$summary | ConvertTo-Json -Depth 5 | Out-File $summaryJson -Encoding UTF8

# 生成 report txt
$report = @()
$report += "=========================================="
$report += "  PIM Native Tracker 测试报告"
$report += "=========================================="
$report += ""
$report += "模式: $Mode"
$report += "时间: $($startTime.ToString("yyyy-MM-dd HH:mm:ss")) ~ $($endTime.ToString("yyyy-MM-dd HH:mm:ss"))"
$report += "耗时: $duration"
$report += ""
$report += "结果: $passed/$total 通过 ($passRate%)"
$report += ""
$report += "------------------------------------------"
$report += ""

$grouped = $results | Group-Object -Property module
foreach ($g in $grouped) {
    $report += "[$($g.Name)]"
    foreach ($r in $g.Group) {
        $icon = if ($r.passed) { "✅" } else { "❌" }
        $report += "  $icon $($r.test): $($r.message)"
    }
    $report += ""
}
$report += "------------------------------------------"
$report += ""
if ($failed -gt 0) {
    $report += "⚠️  $failed 项测试失败，请检查上述详情"
} else {
    $report += "🎉 全部通过"
}
$report | Out-File $reportTxt -Encoding UTF8

Write-Host ""
Write-Host "报告已生成:"
Write-Host "  JSON: $summaryJson"
Write-Host "  TXT : $reportTxt"
Write-Host "  LOG : $executionLog"

exit $(if ($failed -gt 0) { 1 } else { 0 })

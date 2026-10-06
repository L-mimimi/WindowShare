# ============================================================
# WindowShare UIA 双端连接回归（发版纪律）
#
# 用法（仓库根目录执行，需先 dotnet publish 到 dist\publish 或指定 -AppDir）：
#   powershell -ExecutionPolicy Bypass -File scripts\uia-regression.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\uia-regression.ps1 -AppDir "D:\...\dist\publish"
#
# 流程：启动 Host → UIA 点「开始共享」→ 读房间号/密码 →
#       以 CLI 参数（--connect/--password，v1.5.0 Q4）启动 Viewer 自动直连 →
#       处理设备审批弹窗（点「允许并记住」）→ 轮付认证状态 →
#       断言「已连接 + 观看者 1」→ 优雅断开并清理。
# 退出码 0 = PASS，非 0 = FAIL。
# ============================================================
param(
    [string]$AppDir = ""
)
$ErrorActionPreference = "Continue"
$repo = Split-Path -Parent $PSScriptRoot
if ($AppDir -eq "") { $AppDir = Join-Path $repo "dist\publish" }
if (-not (Test-Path (Join-Path $AppDir "WindowShare.Host.exe"))) {
    Write-Error "未找到 $AppDir\WindowShare.Host.exe（先运行 scripts\build.ps1 或手动 publish）"
    exit 2
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$script:pass = $false
function Find-Id($win, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}
function Invoke-Button($win, [string]$id) {
    $b = Find-Id $win $id
    if (-not $b) { throw "控件 $id 不存在" }
    ($b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Get-Text($win, [string]$id) {
    $e = Find-Id $win $id
    if (-not $e) { return "" }
    try { return ($e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value }
    catch { return $e.Current.Name }
}
function Get-ProcWindows([string]$procName) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $wins = @()
    foreach ($p in @(Get-Process $procName -ErrorAction SilentlyContinue)) {
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
        $wins += @($root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond) | ForEach-Object { $_ })
    }
    return $wins
}
function Get-MainWindow([string]$procName, [string]$titleLike) {
    return Get-ProcWindows $procName | Where-Object { $_.Current.Name -like $titleLike } | Select-Object -First 1
}

function Cleanup {
    Stop-Process -Name WindowShare.Host, WindowShare.Viewer, WindowShare.Signaling -Force -ErrorAction SilentlyContinue
}

try {
    Cleanup
    Start-Sleep -Seconds 2

    Write-Host "[1/6] 启动 Host ..." -ForegroundColor Cyan
    Start-Process (Join-Path $AppDir "WindowShare.Host.exe") -WorkingDirectory $AppDir
    $hostWin = $null
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Seconds 1
        $hostWin = Get-MainWindow "WindowShare.Host" "*Host*"
        if ($hostWin) { break }
    }
    if (-not $hostWin) { throw "Host 窗口未出现" }

    Write-Host "[2/6] 开始共享 ..." -ForegroundColor Cyan
    Invoke-Button $hostWin "BtnToggleShare"
    $password = ""
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 1
        $password = Get-Text $hostWin "TxtPassword"
        if ($password.Length -ge 6) { break }
    }
    if ($password.Length -lt 6) { throw "未读到会话密码（共享可能未启动）" }
    Write-Host "      密码已取得，端口 48750" -ForegroundColor DarkGray

    Write-Host "[3/6] 以 CLI 参数启动 Viewer 自动直连 ..." -ForegroundColor Cyan
    Start-Process (Join-Path $AppDir "WindowShare.Viewer.exe") `
        -ArgumentList "--connect", "127.0.0.1:48750", "--password", $password `
        -WorkingDirectory $AppDir

    Write-Host "[4/6] 等待连接（含设备审批处理）..." -ForegroundColor Cyan
    $viewerWin = $null
    $state = ""
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1

        # 首次连接会弹设备审批：代用户点「允许并记住」（发版纪律的一部分）
        $dialog = Get-ProcWindows "WindowShare.Host" |
            Where-Object { $_.Current.Name -eq "观看者请求接入" } | Select-Object -First 1
        if ($dialog) {
            $btns = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::Button)))
            $approve = $btns | Where-Object { $_.Current.Name -match "允许并记住" } | Select-Object -First 1
            if ($approve -and $approve.Current.IsEnabled) {
                ($approve.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
                Write-Host "      已批准设备（允许并记住）" -ForegroundColor DarkGray
            }
        }

        if (-not $viewerWin) { $viewerWin = Get-MainWindow "WindowShare.Viewer" "*Viewer*" }
        if ($viewerWin) {
            $e = Find-Id $viewerWin "TxtState"
            if ($e) { $state = $e.Current.Name }
            if ($state -match "已连接") { break }
        }
    }
    if ($state -notmatch "已连接") { throw "Viewer 未达成连接（最后状态: $state）" }

    Write-Host "[5/6] 断言端到端状态 ..." -ForegroundColor Cyan
    $transport = Get-Text $viewerWin "TxtTransport"
    $bitrate = Get-Text $viewerWin "TxtBitrate"
    $hostStats = Get-Text $hostWin "TxtStats"
    Write-Host "      Viewer: $state | $transport | $bitrate"
    Write-Host "      Host  : $hostStats"
    if ($hostStats -notmatch "观看者：1") { throw "Host 侧观看者数异常: $hostStats" }

    Write-Host "[6/6] 优雅断开 ..." -ForegroundColor Cyan
    Invoke-Button $viewerWin "BtnDisconnect"
    Start-Sleep -Seconds 2

    $script:pass = $true
    Write-Host "==== UIA 回归 PASS ====" -ForegroundColor Green
}
catch {
    Write-Host "==== UIA 回归 FAIL: $($_.Exception.Message) ====" -ForegroundColor Red
    $script:pass = $false
}
finally {
    Cleanup
}

if ($script:pass) { exit 0 } else { exit 1 }

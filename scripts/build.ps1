# ============================================================
# WindowShare 构建脚本
#
# 用法（在仓库根目录执行）：
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1
#       构建 + 单元测试 + 发布 + 冒烟测试
#
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package
#       额外生成安装包 installer\output\WindowShare-Setup-<版本>.exe
#
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Portable
#       额外生成便携版 dist\portable\ + WindowShare-Portable-<版本>.zip
#       （解压即用，数据存程序目录 data\，可直接放 U 盘）
#
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Package -Portable
#       同时生成安装包与便携版
#
#   powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -SkipTests
#       跳过单元测试与冒烟测试（仅构建/发布/打包）
#
# 安装包编译器 ISCC.exe 优先使用系统已安装的 Inno Setup 6/7；
# 未安装时自动通过 NuGet 包 Tools.InnoSetup 获取（无需手工安装）。
# ============================================================
param(
    [switch]$Package,
    [switch]$Portable,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = "1.1.1"

# ---------- 定位 dotnet（必须带 SDK，优先 PATH，其次用户级安装目录） ----------
function Test-DotnetSdk([string]$exe) {
    try {
        $sdks = & $exe --list-sdks 2>$null | Out-String
        return $sdks.Trim().Length -gt 0
    }
    catch { return $false }
}

$dotnet = ""
if (Test-DotnetSdk "dotnet") {
    $dotnet = "dotnet"
}
else {
    $userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if ((Test-Path $userDotnet) -and (Test-DotnetSdk $userDotnet)) {
        $dotnet = $userDotnet
    }
}

if ($dotnet -eq "") {
    throw "未找到可用的 .NET SDK（需要 8.0.x）。请安装：https://dotnet.microsoft.com/download/dotnet/8.0"
}
Write-Host "使用 dotnet: $dotnet" -ForegroundColor DarkGray

Write-Host "==== WindowShare 构建 ====" -ForegroundColor Cyan

# ---------- 1/5 构建 ----------
Write-Host "[1/5] dotnet build ..." -ForegroundColor Yellow
& $dotnet build -c Release -m:1
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

# ---------- 2/5 单元测试 ----------
if ($SkipTests) {
    Write-Host "[2/5] 跳过单元测试" -ForegroundColor DarkYellow
}
else {
    Write-Host "[2/5] dotnet test ..." -ForegroundColor Yellow
    & $dotnet test -c Release --no-build -m:1
    if ($LASTEXITCODE -ne 0) { throw "单元测试失败" }
}

# ---------- 3/5 发布（self-contained，免装运行时） ----------
Write-Host "[3/5] dotnet publish ..." -ForegroundColor Yellow
$pubRoot = Join-Path $root "dist\publish"
if (Test-Path $pubRoot) { Remove-Item $pubRoot -Recurse -Force }

$projects = @("src/WindowShare.Host", "src/WindowShare.Viewer")
foreach ($p in $projects) {
    & $dotnet publish $p -c Release -r win-x64 --self-contained true -o $pubRoot /p:PublishSingleFile=false -m:1
    if ($LASTEXITCODE -ne 0) { throw "发布失败: $p" }
}
& $dotnet publish "src/WindowShare.Signaling" -c Release -r win-x64 --self-contained true -o "$pubRoot\signaling" /p:PublishSingleFile=false -m:1
if ($LASTEXITCODE -ne 0) { throw "发布失败: src/WindowShare.Signaling" }
Write-Host "发布输出: $pubRoot"

# ---------- 4/5 冒烟测试（真实捕获本机屏幕约 30 秒） ----------
if ($SkipTests) {
    Write-Host "[4/5] 跳过冒烟测试" -ForegroundColor DarkYellow
}
else {
    Write-Host "[4/5] 冒烟测试（捕获/编码/传输/解码/信令/WebRTC 回环）..." -ForegroundColor Yellow
    & $dotnet run --project tools/WindowShare.SmokeTest -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw "冒烟测试失败" }
}

# ---------- 5/6 安装包 ----------
if (-not $Package) {
    Write-Host "[5/6] 跳过安装包（使用 -Package 生成）" -ForegroundColor DarkYellow
}
else {
    Write-Host "[5/6] 生成安装包 ..." -ForegroundColor Yellow

    $iscc = ""
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 7\ISCC.exe",
        "C:\Program Files\Inno Setup 7\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $iscc = $c; break }
    }

    if ($iscc -eq "") {
        Write-Host "  未找到已安装的 Inno Setup，通过 NuGet 获取编译器 ..." -ForegroundColor DarkYellow
        $toolDir = Join-Path $root "tools\.packtools"
        New-Item -ItemType Directory -Force -Path $toolDir | Out-Null
        Copy-Item (Join-Path $root "scripts\packtools.csproj") (Join-Path $toolDir "pt.csproj") -Force
        & $dotnet restore $toolDir | Out-Null
        $iscc = Join-Path $env:USERPROFILE ".nuget\packages\tools.innosetup\7.1.0\tools\ISCC.exe"
    }

    if (-not (Test-Path $iscc)) {
        throw "未找到 ISCC.exe（可执行 winget install JRSoftware.InnoSetup 后重试）"
    }

    Write-Host "  使用编译器: $iscc" -ForegroundColor DarkGray
    & $iscc (Join-Path $root "installer\WindowShare.iss")
    if ($LASTEXITCODE -ne 0) { throw "打包失败" }
    Write-Host "安装包目录: $(Join-Path $root 'installer\output')" -ForegroundColor Green
}

# ---------- 6/6 便携版 ----------
if (-not $Portable) {
    Write-Host "[6/6] 跳过便携版（使用 -Portable 生成）" -ForegroundColor DarkYellow
    Write-Host "==== 构建完成 ====" -ForegroundColor Green
    exit 0
}

Write-Host "[6/6] 生成便携版 ..." -ForegroundColor Yellow
$portableRoot = Join-Path $root "dist\portable"
$portableApp = Join-Path $portableRoot "WindowShare-Portable-$version"
if (Test-Path $portableApp) { Remove-Item $portableApp -Recurse -Force }
New-Item -ItemType Directory -Force -Path $portableApp | Out-Null

# 主程序（Host + Viewer 同目录；signaling 子目录随发布一起带过来）
Copy-Item "$pubRoot\*" $portableApp -Recurse -Force

# 便携标记 + 数据目录（存在 portable.marker 或 data\ 即自动进入便携模式）
Copy-Item (Join-Path $root "scripts\portable\portable.marker") $portableApp -Force
New-Item -ItemType Directory -Force -Path (Join-Path $portableApp "data\logs") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $portableApp "data\config") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $portableApp "data\recordings") | Out-Null

# 启动脚本与说明
Copy-Item (Join-Path $root "scripts\portable\启动-共享端.bat") $portableApp -Force
Copy-Item (Join-Path $root "scripts\portable\启动-观看端.bat") $portableApp -Force
Copy-Item (Join-Path $root "scripts\portable\启动-信令服务器.bat") $portableApp -Force
Copy-Item (Join-Path $root "scripts\portable\使用说明.txt") $portableApp -Force

# 打 zip
$zip = Join-Path $portableRoot "WindowShare-Portable-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Write-Host "  压缩中 ..." -ForegroundColor DarkGray
Compress-Archive -Path $portableApp -DestinationPath $zip -CompressionLevel Optimal

$zipMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$fileCount = (Get-ChildItem $portableApp -Recurse -File).Count
Write-Host "便携版目录: $portableApp" -ForegroundColor Green
Write-Host "便携版压缩包: $zip ($zipMb MB, $fileCount 个文件)" -ForegroundColor Green
Write-Host "==== 构建完成 ====" -ForegroundColor Green

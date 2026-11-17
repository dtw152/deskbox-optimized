#requires -Version 5.1
<#
.SYNOPSIS
    一键把改造版 DeskBox 编译成可安装的 .exe（Inno Setup 安装包）。

.DESCRIPTION
    1) 校验 .NET 10 SDK / Rust / Inno Setup 是否就绪；
    2) 调用仓库自带的 scripts\publish-aot-retail.ps1 产出 Native AOT 发布目录；
    3) 调用 Inno Setup 编译 installer\DeskBox.iss；
    4) 把安装包复制到 dist\ 并生成 .sha256。

    全程在 Windows 上执行（WinUI 3 + Native AOT 无法在 Linux/macOS 上编译）。

.PARAMETER Platform
    x64（默认）或 ARM64。

.PARAMETER SkipPublish
    跳过发布步骤，直接用手头已有的发布目录打安装包（调试用）。
#>
[CmdletBinding()]
param(
    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64",

    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runtimeId = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$publishDir = Join-Path $repoRoot ".artifacts\aot-retail\$runtimeId\publish"
$issPath = if ($Platform -eq "ARM64") {
    Join-Path $repoRoot "installer\DeskBox.arm64.iss"
} else {
    Join-Path $repoRoot "installer\DeskBox.iss"
}
$distDir = Join-Path $repoRoot "dist"

function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

function Resolve-IsccPath {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "找不到 ISCC.exe。请安装 Inno Setup 6+（https://jrsoftware.org/isdl.php）后重试。"
}

# ── 0) 环境检查 ─────────────────────────────────────────────
Write-Step "检查编译环境"

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw "未找到 dotnet。请安装 .NET 10 SDK（https://dotnet.microsoft.com/download/dotnet/10.0）。"
}
$sdkVersion = (& dotnet --version).Trim()
Write-Host "dotnet SDK: $sdkVersion"
if ([version]($sdkVersion.Split('-')[0]) -lt [version]"10.0.0") {
    throw "当前 SDK 是 $sdkVersion，仓库 global.json 要求 .NET 10（10.0.402 起），请升级。"
}

if (-not $SkipPublish) {
    $cargo = Get-Command cargo -ErrorAction SilentlyContinue
    if (-not $cargo) {
        throw "未找到 Rust（cargo）。请安装 rustup + MSVC 工具链：winget install Rustlang.Rustup，然后 rustup default stable-x86_64-pc-windows-msvc。"
    }
    Write-Host "Rust: $((& cargo --version).Trim())"
}

$iscc = Resolve-IsccPath
Write-Host "Inno Setup ISCC: $iscc"

# ── 1) Native AOT 发布 ──────────────────────────────────────
if ($SkipPublish) {
    Write-Step "跳过发布，复用现有发布目录"
    if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
        throw "发布目录不存在：$publishDir。去掉 -SkipPublish 先完整构建。"
    }
} else {
    Write-Step "Native AOT 发布（$Platform，首次约 10–20 分钟）"
    & (Join-Path $PSScriptRoot "publish-aot-retail.ps1") -Platform $Platform
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir "DeskBox.exe") -PathType Leaf)) {
        throw "发布失败：$publishDir 下没有 DeskBox.exe，请查看 .artifacts\aot-retail\$runtimeId\publish.log。"
    }
}

# ── 2) 编译安装包 ───────────────────────────────────────────
Write-Step "编译安装包（Inno Setup）"
Push-Location $repoRoot
try {
    & $iscc `
        /DDeskBoxNativeAot=1 `
        /DDeskBoxBundledRuntime=1 `
        "/DMyAppReleaseDir=$publishDir" `
        $issPath
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC 编译失败（退出码 $LASTEXITCODE）。"
    }
}
finally {
    Pop-Location
}

# ── 3) 收尾：复制到 dist 并出校验值 ──────────────────────────
Write-Step "输出安装包"
$outputDir = Join-Path $repoRoot "Output"
$installer = Get-ChildItem -LiteralPath $outputDir -Filter "*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $installer) {
    throw "没有在 $outputDir 找到生成的安装包。"
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null
$target = Join-Path $distDir $installer.Name
Copy-Item -LiteralPath $installer.FullName -Destination $target -Force
$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
"$hash  $($installer.Name)" | Set-Content -LiteralPath "$target.sha256" -Encoding utf8

$sizeMb = [math]::Round($installer.Length / 1MB, 1)
Write-Host ""
Write-Host "完成：$target" -ForegroundColor Green
Write-Host "大小：$sizeMb MB"
Write-Host "SHA256：$hash"
Write-Host ""
Write-Host "安装：双击 $($installer.Name)，当前用户安装，无需管理员权限。"
Write-Host "未做代码签名，首次运行可能出现 SmartScreen 提示（更多信息 → 仍要运行）。"

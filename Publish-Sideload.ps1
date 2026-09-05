<#
.SYNOPSIS
  为 C99 生成可旁加载（不进微软商店）的 Release 版 MSIX，其它电脑可直接安装。

.DESCRIPTION
  调用 Visual Studio 的 MSBuild 以 "SideloadOnly" 模式打包，并签名。产物输出到
  .\AppPackages\C99_<版本>_<架构>_Test\ 下，包含：
    - C99_<版本>_<架构>.msix   (安装包，自包含 Windows App Runtime/.NET，单文件即可分发)
    - C99_<版本>_<架构>.cer    (分发证书，其它电脑需信任一次)
    - Install.ps1 / Add-AppDevPackage.ps1（一键安装脚本，可选）

  自包含（WindowsAppSDKSelfContained=true）后包内已含 Windows App Runtime，
  不再生成 Dependencies 依赖文件夹，一个 .msix 即可在装配时无需单独装运行时。

  ⚠ 版本号写回 Package.appxmanifest（打包工具以清单版本为准），
    每发布一次会在仓库中留下一次版本号改动，建议随代码一起提交。

.PARAMETER Architecture
  目标架构：x64（默认）/ x86 / arm64

.PARAMETER Configuration
  Release（默认）/ Debug

.PARAMETER Version
  完整的 4 段版本号，例如 1.0.3.0。缺省时自动把清单版本的末位 +1。

.PARAMETER NoBump
  不自动递增版本号，使用清单中原版本。

.PARAMETER OpenOutput
  打包完成后用资源管理器打开输出目录。

.PARAMETER CertificatePfx
  可选：签名证书 .pfx 的完整路径。缺省时使用 Windows 证书存储中
  由 C99.csproj 里 PackageCertificateThumbprint 指定的证书（本机已导入）。

.PARAMETER CertificatePassword
  CertificatePfx 对应的密码（若有）。

.EXAMPLE
  .\Publish-Sideload.ps1
  .\Publish-Sideload.ps1 -Version 2.0.0.0
  .\Publish-Sideload.ps1 -Architecture arm64 -Version 1.1.0.0

.NOTES
  前置要求：
    1. 本机安装 Visual Studio 2022（含 UWP/桌面 + MSIX 打包组件）。
    2. python 已在 PATH（XAML 编译补丁依赖，见 fix_xaml_input.py）。
#>

[CmdletBinding()]
param(
    [ValidateSet("x64", "x86", "arm64")]
    [string]$Architecture = "x64",

    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [Version]$Version,

    [switch]$NoBump,

    [switch]$OpenOutput,

    [string]$CertificatePfx,

    [string]$CertificatePassword
)

$ErrorActionPreference = "Stop"

$projectRoot  = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile  = Join-Path $projectRoot "C99.csproj"
$manifestFile = Join-Path $projectRoot "Package.appxmanifest"

if (-not (Test-Path $projectFile))  { throw "未找到项目文件: $projectFile" }
if (-not (Test-Path $manifestFile)) { throw "未找到清单文件: $manifestFile" }

if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
    Write-Warning "未在 PATH 中找到 python，XAML 编译修复步骤可能会失败（见 fix_xaml_input.py）。"
}

# ---------- 定位 MSBuild（vswhere 优先） ----------
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" }
if (-not (Test-Path $vswhere)) { throw "未找到 vswhere.exe，请先安装 Visual Studio。" }

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "未找到 MSBuild.exe，请安装 Visual Studio 2022 并勾选 MSIX 打包相关组件。" }

# ---------- 架构 -> 平台 / RID ----------
$platformMap = @{ x64 = "x64";   x86 = "x86";   arm64 = "ARM64" }
$ridMap      = @{ x64 = "win-x64"; x86 = "win-x86"; arm64 = "win-arm64" }
$platform = $platformMap[$Architecture]
$rid      = $ridMap[$Architecture]

# ---------- 版本号（写入 Package.appxmanifest） ----------
$manifestBytes = [System.IO.File]::ReadAllBytes($manifestFile)
$hasBom = $manifestBytes.Length -ge 3 -and $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF
$manifestText = [System.Text.Encoding]::UTF8.GetString(($(if ($hasBom) { $manifestBytes[3..($manifestBytes.Length-1)] } else { $manifestBytes })))

$current = [Version]([regex]::Match($manifestText, 'Version="(\d+\.\d+\.\d+\.\d+)"').Groups[1].Value)

$Version = if ($Version) {
    $Version
}
elseif (-not $NoBump) {
    [Version]::new($current.Major, $current.Minor, $current.Build, $current.Revision + 1)
}
else {
    $current
}

if ($Version -ne $current) {
    # 仅匹配 "Identity" 的 Version 属性（前置字符不能是字母，避免误伤 MinVersion/MaxVersion）
    $manifestText = $manifestText -replace '(?<![A-Za-z])Version="(\d+\.\d+\.\d+\.\d+)"', "Version=""$Version"""
}

Write-Host ""
Write-Host "======== C99 MSIX 旁加载打包 ========" -ForegroundColor Cyan
Write-Host "MSBuild : $msbuild"
Write-Host "平台    : $Configuration / $Architecture ($rid)"
Write-Host "版本    : $current -> $Version (写回 Package.appxmanifest: $($Version -ne $current))"
Write-Host "=====================================" -ForegroundColor Cyan
Write-Host ""

if ($Version -ne $current) {
    $enc = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($manifestFile, $manifestText, $enc)
    if ($hasBom) {
        $bom = [byte[]](0xEF, 0xBB, 0xBF)
        [System.IO.File]::WriteAllBytes($manifestFile, $bom + [System.IO.File]::ReadAllBytes($manifestFile))
    }
}

# ---------- 打包 ----------
$args = @(
    $projectFile,
    "/restore",
    "/t:Rebuild",
    "/nologo",
    "/verbosity:minimal",
    "/p:Configuration=$Configuration",
    "/p:Platform=$platform",
    "/p:RuntimeIdentifier=$rid",
    "/p:AppxPackage=true",
    "/p:GenerateAppxPackageOnBuild=true",
    "/p:AppxBundle=Never",
    "/p:UapAppxPackageBuildMode=SideloadOnly",
    "/p:AppxPackageSigningEnabled=true",
    "/p:AppxAutoIncrementPackageRevision=false"
)

if ($CertificatePfx) {
    if (-not (Test-Path $CertificatePfx)) { throw "未找到证书文件: $CertificatePfx" }
    $args += "/p:PackageCertificateKeyFile=$CertificatePfx"
    $args += "/p:PackageCertificatePassword=$CertificatePassword"
    Write-Host "签名    : 证书文件 $CertificatePfx"
}
else {
    # 缺省使用本机证书存储中 csproj 指定的 PackageCertificateThumbprint
    $thumbprint = ([xml](Get-Content -Raw $projectFile)).Project.PropertyGroup | ForEach-Object { $_.PackageCertificateThumbprint } | Where-Object { $_ } | Select-Object -First 1
    if (-not $thumbprint) { throw "未能从 C99.csproj 读取 PackageCertificateThumbprint。" }
    $args += "/p:PackageCertificateThumbprint=$thumbprint"
    Write-Host "签名    : 本机证书存储（thx=$thumbprint）"
}

& $msbuild $args
if ($LASTEXITCODE -ne 0) {
    Write-Error "打包失败，MSBuild 退出码：$LASTEXITCODE"
    exit $LASTEXITCODE
}

# ---------- 输出摘要 ----------
$appPackagesDir = Join-Path $projectRoot "AppPackages"
$expectedFolder = "C99_$Version`_$Architecture`_Test"
$newest = @(
    Get-ChildItem -Path $appPackagesDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $expectedFolder }
) + @(
    Get-ChildItem -Path $appPackagesDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "_${Architecture}_Test$" } |
        Sort-Object LastWriteTime -Descending
)
$newest = @($newest | Where-Object { $_ }) | Select-Object -First 1

Write-Host ""
Write-Host "打包完成，输出目录：$appPackagesDir" -ForegroundColor Green
if ($newest) {
    Get-ChildItem -Path $newest.FullName -File | Where-Object { $_.Extension -in ".msix", ".cer" } | ForEach-Object {
        Write-Host "  $($_.FullName)" -ForegroundColor Green
    }
    if ($OpenOutput) { Start-Process explorer.exe $newest.FullName }
}
else {
    Write-Warning "未能在 AppPackages 下找到本次产物，请检查上方 MSBuild 输出。"
}
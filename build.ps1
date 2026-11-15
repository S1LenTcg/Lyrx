[CmdletBinding()]
param (
    [Parameter(Position = 0)]
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [Parameter(Position = 1)]
    [string]$Runtime = "win-x64",

    [Parameter()]
    [switch]$SingleFile,

    [Parameter()]
    [switch]$NoSelfContained,

    [Parameter()]
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

# 获取仓库根目录
$repoRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $repoRoot) { $repoRoot = Get-Location }

# 1. 检查 dotnet 环境
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "[错误] 系统未安装 .NET SDK，或未将其添加到 PATH 环境变量中。" -ForegroundColor Red
    Write-Host "请前往下载并安装 .NET 8.0 SDK (或更高版本):" -ForegroundColor Yellow
    Write-Host "https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Cyan
    exit 1
}

# 2. 检查 .NET SDK 版本
$sdks = & dotnet --list-sdks 2>$null
$hasNet8OrHigher = $false
foreach ($line in $sdks) {
    if ($line -match '^(\d+)\.') {
        $major = [int]$Matches[1]
        if ($major -ge 8) {
            $hasNet8OrHigher = $true
            break
        }
    }
}

if (-not $hasNet8OrHigher) {
    Write-Host "[警告] 未检测到 .NET 8.0 或更高版本的 SDK，当前已安装的 SDK:" -ForegroundColor Yellow
    Write-Host ($sdks -join "`n")
    Write-Host "如果编译遇到问题，请安装 .NET 8.0 SDK: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
}

# 3. 输出目录设置
$outDir = if ($OutputDir) { $OutputDir } else { Join-Path $repoRoot "dist" }
$projectFile = Join-Path $repoRoot "src\Lyrx.Windows\Lyrx.Windows.csproj"

if (-not (Test-Path $projectFile)) {
    Write-Host "[错误] 找不到项目文件: $projectFile" -ForegroundColor Red
    exit 1
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " 开始发布 Lyrx" -ForegroundColor Cyan
Write-Host " 配置: $Configuration" -ForegroundColor Gray
Write-Host " 架构: $Runtime" -ForegroundColor Gray
Write-Host " 独立运行时: $(-not $NoSelfContained)" -ForegroundColor Gray
Write-Host " 单文件打包: $([bool]$SingleFile)" -ForegroundColor Gray
Write-Host " 输出目录: $outDir" -ForegroundColor Gray
Write-Host "==========================================" -ForegroundColor Cyan

# 4. 组装 dotnet publish 参数
$publishArgs = @(
    "publish",
    "`"$projectFile`"",
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", "`"$outDir`""
)

if (-not $NoSelfContained) {
    $publishArgs += "--self-contained"
} else {
    $publishArgs += "--no-self-contained"
}

if ($SingleFile) {
    $publishArgs += "-p:PublishSingleFile=true"
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
    $publishArgs += "-p:EnableCompressionInSingleFile=true"
}

# 5. 执行编译发布
$publishCommandLine = "dotnet " + ($publishArgs -join " ")
Write-Host "正在执行: $publishCommandLine" -ForegroundColor DarkGray

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "[失败] 编译发布未能完成，错误码: $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host " 编译发布成功！" -ForegroundColor Green
Write-Host " 可执行文件: $outDir\Lyrx.exe" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green

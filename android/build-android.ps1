param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Install
)

$ErrorActionPreference = "Stop"

$solutionPath = Join-Path $PSScriptRoot "Lyrx.Android.sln"
if (-not (Test-Path $solutionPath)) {
    throw "未找到 Android 解决方案: $solutionPath"
}

function Resolve-AndroidSdk {
    $candidates = @(
        $env:ANDROID_HOME,
        $env:ANDROID_SDK_ROOT,
        "D:\IDE\Android\plugin",
        (Join-Path $env:LOCALAPPDATA "Android\Sdk")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($dir in $candidates) {
        if ((Test-Path (Join-Path $dir "platforms")) -and (Test-Path (Join-Path $dir "platform-tools"))) {
            return $dir
        }
    }
    return $null
}

function Resolve-JavaSdk {
    $candidates = @(
        $env:JAVA_HOME,
        "D:\IDE\Android\Android Studio\jbr"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($dir in $candidates) {
        if (Test-Path (Join-Path $dir "bin\javac.exe")) {
            return $dir
        }
    }
    return $null
}

$workloads = & dotnet workload list 2>&1 | Out-String
if ($workloads -notmatch "(?m)^\s*android\b") {
    Write-Host ""
    Write-Host "[Lyrx.Android] 未检测到 .NET Android 工作负载 (android workload)。" -ForegroundColor Yellow
    Write-Host "请以【管理员身份】打开 PowerShell 或 CMD，然后运行：" -ForegroundColor Cyan
    Write-Host "    dotnet workload install android" -ForegroundColor White
    Write-Host ""
    exit 1
}

$androidSdk = Resolve-AndroidSdk
if (-not $androidSdk) {
    Write-Host ""
    Write-Host "[Lyrx.Android] 未找到 Android SDK（需包含 platforms 与 platform-tools）。" -ForegroundColor Yellow
    Write-Host "请设置环境变量 ANDROID_HOME，或在 Android Studio 中安装 Android SDK。" -ForegroundColor Cyan
    Write-Host ""
    exit 1
}

$msbuildArgs = @(
    $solutionPath,
    "-c", $Configuration,
    "-p:AndroidSdkDirectory=$androidSdk"
)

$javaSdk = Resolve-JavaSdk
if ($javaSdk) {
    $msbuildArgs += "-p:JavaSdkDirectory=$javaSdk"
}

if ($Install) {
    $msbuildArgs += "-t:Install"
}

Write-Host "[Lyrx.Android] Solution   : $solutionPath"
Write-Host "[Lyrx.Android] Config     : $Configuration"
Write-Host "[Lyrx.Android] AndroidSDK : $androidSdk"
if ($javaSdk) {
    Write-Host "[Lyrx.Android] JavaSDK    : $javaSdk"
}

& dotnet build @msbuildArgs
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

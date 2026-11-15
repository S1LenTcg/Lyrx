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

function Get-CsprojProperty([xml]$Xml, [string]$Name) {
    $node = $Xml.SelectSingleNode("//PropertyGroup/$Name")
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        return $null
    }
    return $node.InnerText.Trim()
}

if ($Configuration -eq "Release") {
    $csprojPath = Join-Path $PSScriptRoot "Lyrx.Android\Lyrx.Android.csproj"
    [xml]$csprojXml = Get-Content $csprojPath -Raw -Encoding UTF8
    $appTitle = Get-CsprojProperty $csprojXml "ApplicationTitle"
    $appVersion = Get-CsprojProperty $csprojXml "ApplicationDisplayVersion"
    $appId = Get-CsprojProperty $csprojXml "ApplicationId"

    if ($appTitle -and $appVersion -and $appId) {
        $outDir = Join-Path $PSScriptRoot "Lyrx.Android\bin\$Configuration\net10.0-android"
        $namedApk = Join-Path $outDir "$appTitle V$appVersion.apk"
        $signedRawApk = Join-Path $outDir "$appId-Signed.apk"

        if (Test-Path $signedRawApk) {
            Copy-Item -Path $signedRawApk -Destination $namedApk -Force
        }

        # 仅保留带签名的正式版本输出，清理中间未签名及临时签名包
        foreach ($staleFile in @(
            (Join-Path $outDir "$appId.apk"),
            $signedRawApk,
            "$signedRawApk.idsig"
        )) {
            if (Test-Path $staleFile) {
                Remove-Item -Path $staleFile -Force
            }
        }

        if (Test-Path $namedApk) {
            Write-Host "[Lyrx.Android] Output APK : $namedApk" -ForegroundColor Green
        }
    }
}

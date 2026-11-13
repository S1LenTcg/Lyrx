$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$toolsRoot = "D:\antigravity\regular\Musebase\build-tools"

if (Test-Path "$toolsRoot\dotnet") {
    $env:DOTNET_CLI_HOME = "$toolsRoot\cli-state"
    $env:NUGET_PACKAGES = "$toolsRoot\nuget"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $env:Path = "$toolsRoot\dotnet;$env:Path"
}

$outDir = Join-Path $repoRoot "dist"
Write-Host "Publishing Lyrx to $outDir ..." -ForegroundColor Cyan

if (Test-Path "$toolsRoot\nuget") {
    dotnet publish "$repoRoot\src\Lyrx.Windows\Lyrx.Windows.csproj" `
        -c Release -r win-x64 --self-contained `
        -m:1 -p:UseSharedCompilation=false `
        --source "$toolsRoot\nuget" `
        -o $outDir
} else {
    dotnet publish "$repoRoot\src\Lyrx.Windows\Lyrx.Windows.csproj" `
        -c Release -r win-x64 --self-contained `
        -o $outDir
}

Write-Host "Done! Executable: $outDir\Lyrx.exe" -ForegroundColor Green

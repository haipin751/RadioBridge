param(
    [Parameter(Mandatory = $true, HelpMessage = "Path to your Radio Commander installation directory (containing 'Radio Commander_Data' and 'BepInEx').")]
    [string]$GameDir,

    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc. Ensure .NET Framework 4.x Developer/Runtime tools are installed on Windows."
}

$managed = Join-Path $GameDir "Radio Commander_Data\Managed"
$bepCore = Join-Path $GameDir "BepInEx\core"
$bepPlugins = Join-Path $GameDir "BepInEx\plugins"

if (-not (Test-Path $managed)) {
    throw "Managed directory not found: $managed"
}
if (-not (Test-Path $bepCore)) {
    throw "BepInEx core directory not found: $bepCore. Please install BepInEx 5 (x64) into the game directory first."
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root "src\RadioBridge.cs"
$outDir = Join-Path $root "build"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outDll = Join-Path $outDir "RadioBridge.dll"

& $csc /noconfig /nostdlib /target:library /out:"$outDll" `
    /r:"$managed\mscorlib.dll" `
    /r:"$managed\System.dll" `
    /r:"$managed\System.Core.dll" `
    /r:"$managed\UnityEngine.dll" `
    /r:"$managed\UnityEngine.CoreModule.dll" `
    /r:"$managed\UnityEngine.UI.dll" `
    /r:"$bepCore\BepInEx.dll" `
    /r:"$bepCore\0Harmony.dll" `
    "$src"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Build succeeded: $outDll" -ForegroundColor Green

if ($Deploy) {
    if (-not (Test-Path $bepPlugins)) {
        New-Item -ItemType Directory -Force -Path $bepPlugins | Out-Null
    }
    $targetDll = Join-Path $bepPlugins "RadioBridge.dll"
    if (Test-Path $targetDll) {
        Move-Item $targetDll "$env:TEMP\RadioBridge_old_$(Get-Random).dll" -Force -ErrorAction SilentlyContinue
    }
    Copy-Item $outDll $targetDll -Force
    Write-Host "Deployed to: $targetDll" -ForegroundColor Cyan
}

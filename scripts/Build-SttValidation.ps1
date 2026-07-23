param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$validationRoot = Join-Path $repoRoot "artifacts\validation"
$modsRoot = Join-Path $validationRoot "Mods"
$modOutput = Join-Path $modsRoot "GBFR.ChatOverlay"
$archivePath = Join-Path $validationRoot "GBFR.ChatOverlay-0.2.0-stt-base.zip"
$expectedModelSha256 = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE"

& (Join-Path $PSScriptRoot "Prepare-SttRuntime.ps1") -Configuration Release

if (Test-Path -LiteralPath $validationRoot) {
    $resolvedArtifacts = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts")).TrimEnd('\') + '\'
    $resolvedValidation = [IO.Path]::GetFullPath($validationRoot)
    if (-not $resolvedValidation.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a validation directory outside artifacts."
    }
    Remove-Item -LiteralPath $validationRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $modsRoot | Out-Null

$previousReloadedMods = $env:RELOADEDIIMODS
try {
    $env:RELOADEDIIMODS = $modsRoot
    dotnet build (Join-Path $repoRoot "GBFR.ChatOverlay.csproj") `
        --configuration Release `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "Overlay build failed with exit code $LASTEXITCODE."
    }

    if (-not $SkipTests) {
        dotnet test (Join-Path $repoRoot "tests\GBFR.ChatOverlay.Tests\GBFR.ChatOverlay.Tests.csproj") `
            --configuration Release `
            -p:DebugType=None `
            -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) {
            throw "Overlay tests failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    $env:RELOADEDIIMODS = $previousReloadedMods
}

$requiredFiles = @(
    (Join-Path $modOutput "GBFR.ChatOverlay.dll"),
    (Join-Path $modOutput "ModConfig.json"),
    (Join-Path $modOutput "SttRuntime\worker\GBFR.ChatOverlay.SttWorker.exe"),
    (Join-Path $modOutput "SttRuntime\worker\GBFR.ChatOverlay.SttWorker.dll"),
    (Join-Path $modOutput "SttRuntime\worker\GBFR.ChatOverlay.SttWorker.deps.json"),
    (Join-Path $modOutput "SttRuntime\worker\GBFR.ChatOverlay.SttWorker.runtimeconfig.json"),
    (Join-Path $modOutput "SttRuntime\whisper\whisper-cli.exe"),
    (Join-Path $modOutput "SttRuntime\whisper\whisper.dll"),
    (Join-Path $modOutput "SttRuntime\whisper\ggml.dll"),
    (Join-Path $modOutput "SttRuntime\whisper\ggml-base.dll"),
    (Join-Path $modOutput "SttRuntime\models\ggml-base.bin"),
    (Join-Path $modOutput "SttRuntime\runtime-manifest.json"),
    (Join-Path $modOutput "SttRuntime\licenses\OpenAI-Whisper-LICENSE.txt"),
    (Join-Path $modOutput "SttRuntime\licenses\whisper.cpp-LICENSE.txt"),
    (Join-Path $modOutput "SttRuntime\licenses\NAudio-LICENSE.txt")
)
foreach ($path in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Validation package is missing $path"
    }
}

$cpuBackends = Get-ChildItem -LiteralPath (Join-Path $modOutput "SttRuntime\whisper") `
    -Filter "ggml-cpu-*.dll" `
    -File
if ($cpuBackends.Count -eq 0) {
    throw "Validation package has no whisper.cpp CPU backend."
}

$packagedConfig = Get-Content -LiteralPath (Join-Path $modOutput "ModConfig.json") -Raw |
    ConvertFrom-Json
if ($packagedConfig.ModDll -ne "GBFR.ChatOverlay.dll" -or
    -not [string]::IsNullOrEmpty($packagedConfig.ModR2RManagedDll32) -or
    -not [string]::IsNullOrEmpty($packagedConfig.ModR2RManagedDll64)) {
    throw "ModConfig must use the managed ModDll entry and leave ReadyToRun entries empty."
}

$modelPath = Join-Path $modOutput "SttRuntime\models\ggml-base.bin"
$actualModelSha256 = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash
if (-not $actualModelSha256.Equals($expectedModelSha256, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Packaged Whisper base model hash is invalid."
}

Compress-Archive -LiteralPath $modOutput -DestinationPath $archivePath -CompressionLevel Fastest
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Write-Host "Validation folder: $modOutput"
Write-Host "Validation archive: $archivePath"
Write-Host "Archive SHA-256: $archiveHash"

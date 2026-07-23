param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "SttRuntime"))

$cacheRoot = Join-Path $repoRoot "artifacts\stt-cache"
$archivePath = Join-Path $cacheRoot "whisper-bin-x64-v1.9.1.zip"
$extractRoot = Join-Path $cacheRoot "whisper-bin-x64-v1.9.1"
$modelCachePath = Join-Path $cacheRoot "ggml-base.bin"
$workerProject = Join-Path $repoRoot "SttWorker\GBFR.ChatOverlay.SttWorker.csproj"
$licenseRoot = Join-Path $repoRoot "licenses"

$archiveSha256 = "7D8BE46ECD31828E1EB7A2ECDD0D6B314FEAFD82163038AB6092594B0A063539"
$modelSha256 = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE"
$archiveUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip"
$modelUrls = @(
    "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
    # Fallback mirror only; the official model hash below remains authoritative.
    "https://www.telestream.net/download-files/ggml/ggml-base.bin"
)

function Test-PinnedHash([string]$Path, [string]$ExpectedSha256) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    return $actual.Equals($ExpectedSha256, [StringComparison]::OrdinalIgnoreCase)
}

function Get-PinnedDownload([string]$Path, [string[]]$Urls, [string]$ExpectedSha256) {
    if (Test-PinnedHash $Path $ExpectedSha256) {
        Write-Host "Using verified cache: $Path"
        return
    }

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }

    $lastError = $null
    foreach ($url in $Urls) {
        try {
            Write-Host "Downloading $url"
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $Path
            if (-not (Test-PinnedHash $Path $ExpectedSha256)) {
                throw "SHA-256 validation failed for $url"
            }
            return
        }
        catch {
            $lastError = $_
            if (Test-Path -LiteralPath $Path) {
                Remove-Item -LiteralPath $Path -Force
            }
            Write-Warning $_
        }
    }

    throw "No verified download source succeeded. Last error: $lastError"
}

New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null
New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null

Get-PinnedDownload $archivePath @($archiveUrl) $archiveSha256
Get-PinnedDownload $modelCachePath $modelUrls $modelSha256

if (Test-Path -LiteralPath $extractRoot) {
    $resolvedCache = [IO.Path]::GetFullPath($cacheRoot).TrimEnd('\') + '\'
    $resolvedExtract = [IO.Path]::GetFullPath($extractRoot)
    if (-not $resolvedExtract.StartsWith($resolvedCache, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear an extraction directory outside the STT cache."
    }
    Remove-Item -LiteralPath $extractRoot -Recurse -Force
}
Expand-Archive -LiteralPath $archivePath -DestinationPath $extractRoot

$releaseRoot = Join-Path $extractRoot "Release"
$whisperOutput = Join-Path $runtimeRoot "whisper"
$modelOutput = Join-Path $runtimeRoot "models"
$workerOutput = Join-Path $runtimeRoot "worker"
$licensesOutput = Join-Path $runtimeRoot "licenses"
New-Item -ItemType Directory -Force -Path $whisperOutput, $modelOutput, $workerOutput, $licensesOutput | Out-Null

$requiredWhisperFiles = @("whisper-cli.exe", "whisper.dll", "ggml.dll", "ggml-base.dll")
foreach ($fileName in $requiredWhisperFiles) {
    Copy-Item -LiteralPath (Join-Path $releaseRoot $fileName) -Destination $whisperOutput -Force
}
Get-ChildItem -LiteralPath $releaseRoot -Filter "ggml-cpu-*.dll" -File |
    Copy-Item -Destination $whisperOutput -Force

Copy-Item -LiteralPath $modelCachePath -Destination (Join-Path $modelOutput "ggml-base.bin") -Force
Copy-Item -LiteralPath (Join-Path $licenseRoot "OpenAI-Whisper-LICENSE.txt") -Destination $licensesOutput -Force
Copy-Item -LiteralPath (Join-Path $licenseRoot "whisper.cpp-LICENSE.txt") -Destination $licensesOutput -Force
Copy-Item -LiteralPath (Join-Path $licenseRoot "NAudio-LICENSE.txt") -Destination $licensesOutput -Force

dotnet publish $workerProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    --output $workerOutput
if ($LASTEXITCODE -ne 0) {
    throw "STT worker publish failed with exit code $LASTEXITCODE."
}

$runtimeManifest = [ordered]@{
    protocolVersion = 1
    whisperCppVersion = "1.9.1"
    whisperArchiveSha256 = $archiveSha256
    model = "OpenAI Whisper base multilingual"
    modelFile = "models/ggml-base.bin"
    modelSha256 = $modelSha256
    inference = "CPU only"
}
$runtimeManifest |
    ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $runtimeRoot "runtime-manifest.json") -Encoding UTF8

Write-Host "Prepared verified STT runtime at $runtimeRoot"

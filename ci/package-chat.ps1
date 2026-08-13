[CmdletBinding()]
param(
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

if ([string]::IsNullOrWhiteSpace($Version)) {
    $modConfigPath = Join-Path $root 'ModConfig.json'
    if (-not (Test-Path -LiteralPath $modConfigPath -PathType Leaf)) {
        throw "Missing ModConfig.json: $modConfigPath"
    }
    $Version = (Get-Content -Raw -LiteralPath $modConfigPath | ConvertFrom-Json).ModVersion
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Release version must be a stable semantic version such as 0.6.0: $Version"
}

$projectPath = Join-Path $root 'GBFR.ChatOverlay.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Missing project file: $projectPath"
}

$artifactsDir = Join-Path $root 'artifacts'
$packageRoot = Join-Path $artifactsDir 'package'
$packageDir = Join-Path $packageRoot 'GBFR.ChatOverlay'
$zipPath = Join-Path $artifactsDir "GBFR.ChatOverlay-$Version-Relink-2.0.4.zip"
$checksumPath = "$zipPath.sha256"
$previousReloadedMods = $env:RELOADEDIIMODS
$tempManaged = Join-Path $artifactsDir 'managed'
$tempReloadedMods = Join-Path $artifactsDir 'reloaded-mods'
$env:RELOADEDIIMODS = $tempReloadedMods

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "Not a PE image: $Path"
        }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6)) {
            throw "Invalid PE header offset in $Path"
        }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "Invalid PE signature in $Path"
        }
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
    }
}

New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
if (Test-Path -LiteralPath $packageRoot -PathType Container) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath -PathType Leaf) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $checksumPath -PathType Leaf) {
    Remove-Item -LiteralPath $checksumPath -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

try {
    & dotnet publish $projectPath `
        -c Release `
        --self-contained false `
        -o $packageDir `
        -p:OutputPath="$tempManaged"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $packageDir -Recurse -File |
        Where-Object { $_.Extension -in '.pdb', '.xml' } |
        Remove-Item -Force

    $runtimesPath = Join-Path $packageDir 'runtimes'
    if (Test-Path -LiteralPath $runtimesPath -PathType Container) {
        Get-ChildItem -LiteralPath $runtimesPath -Directory |
            Where-Object { $_.Name -ne 'win-x64' } |
            Remove-Item -Recurse -Force
    }

    foreach ($document in @('README.md', 'CHANGELOG.md', 'THIRD_PARTY_NOTICES.md')) {
        $sourcePath = Join-Path $root $document
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Required release document was not found: $sourcePath"
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $packageDir $document) -Force
    }

    $documentationPath = Join-Path $root 'docs'
    if (-not (Test-Path -LiteralPath $documentationPath -PathType Container)) {
        throw "Required documentation directory was not found: $documentationPath"
    }
    Copy-Item -LiteralPath $documentationPath -Destination (Join-Path $packageDir 'docs') -Recurse -Force

    foreach ($requiredFile in @(
        'GBFR.ChatOverlay.dll',
        'GBFR.ChatOverlay.Native.dll',
        'GBFR.ChatOverlay.ConfiguratorUI.dll',
        'GBFR.OverlayHub.Contracts.dll',
        'ModConfig.json',
        'Icon.png',
        'README.md',
        'CHANGELOG.md',
        'THIRD_PARTY_NOTICES.md',
        'docs/index.md',
        'docs/reference/relink-2.0.4-addresses.md',
        'runtimes/win-x64/native/cimgui.dll'
    )) {
        $requiredPath = Join-Path $packageDir $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required release file was not published: $requiredPath"
        }
    }

    $packagedVersion = (
        Get-Content -Raw -LiteralPath (Join-Path $packageDir 'ModConfig.json') |
        ConvertFrom-Json
    ).ModVersion
    if ($packagedVersion -ne $Version) {
        throw "Package version mismatch: requested $Version, ModConfig contains $packagedVersion."
    }
    $packagedConfig = Get-Content -Raw -LiteralPath (Join-Path $packageDir 'ModConfig.json') |
        ConvertFrom-Json
    if ($packagedConfig.ModIcon -ne 'Icon.png') {
        throw "Package icon metadata is not the stable Icon.png asset."
    }

    $managedAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName(
        (Join-Path $packageDir 'GBFR.ChatOverlay.dll')).Version
    if ($managedAssemblyVersion -ne [Version]::Parse($Version + '.0')) {
        throw "Managed assembly version mismatch: $managedAssemblyVersion"
    }
    $configuratorAssemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName(
        (Join-Path $packageDir 'GBFR.ChatOverlay.ConfiguratorUI.dll')).Version
    if ($configuratorAssemblyVersion -ne [Version]::Parse($Version + '.0')) {
        throw "Configurator assembly version mismatch: $configuratorAssemblyVersion"
    }

    $nativeBridgePath = Join-Path $packageDir 'GBFR.ChatOverlay.Native.dll'
    $nativeMachine = Get-PeMachine -Path $nativeBridgePath
    if ($nativeMachine -ne 0x8664) {
        throw ('Native bridge is not x64 (machine=0x{0:X4}): {1}' -f $nativeMachine, $nativeBridgePath)
    }

    foreach ($forbiddenPath in @(
        'Preview.png',
        'docs/CHAT_BRIDGE.md',
        'docs/SESSION_HANDOFF_2026-07-25.md',
        'docs/SMOKE_TEST.md',
        'docs/VOICE_TRANSPORT.md',
        'docs/VOICE_TROUBLESHOOTING_MATRIX.md'
    )) {
        if (Test-Path -LiteralPath (Join-Path $packageDir $forbiddenPath)) {
            throw "Development-only file leaked into the release package: $forbiddenPath"
        }
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipStream = [System.IO.File]::Open(
        $zipPath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    $archive = [System.IO.Compression.ZipArchive]::new(
        $zipStream,
        [System.IO.Compression.ZipArchiveMode]::Create,
        $false)
    try {
        Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
            Sort-Object FullName |
            ForEach-Object {
                $entryName = $_.FullName.Substring($packageRoot.Length + 1).
                    Replace([System.IO.Path]::DirectorySeparatorChar, [char]'/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive,
                    $_.FullName,
                    $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
    }
    finally {
        $archive.Dispose()
        $zipStream.Dispose()
    }
    $zipItem = Get-Item -LiteralPath $zipPath
    if ($zipItem.Length -le 0) {
        throw "ZIP was created but is empty: $zipPath"
    }

    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content `
        -LiteralPath $checksumPath `
        -Value "$zipHash  $($zipItem.Name)" `
        -Encoding Ascii

    Write-Output "ZIP: $zipPath"
    Write-Output "SHA256: $checksumPath"
}
finally {
    if ($null -eq $previousReloadedMods) {
        Remove-Item Env:RELOADEDIIMODS -ErrorAction SilentlyContinue
    }
    else {
        $env:RELOADEDIIMODS = $previousReloadedMods
    }
    if (Test-Path -LiteralPath $tempManaged -PathType Container) {
        Remove-Item -LiteralPath $tempManaged -Recurse -Force
    }
    if (Test-Path -LiteralPath $tempReloadedMods -PathType Container) {
        Remove-Item -LiteralPath $tempReloadedMods -Recurse -Force
    }
    if (Test-Path -LiteralPath $packageRoot -PathType Container) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }
}

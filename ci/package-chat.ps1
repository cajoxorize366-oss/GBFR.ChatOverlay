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

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    throw "Invalid version: $Version"
}

$projectPath = Join-Path $root 'GBFR.ChatOverlay.csproj'
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Missing project file: $projectPath"
}

$artifactsDir = Join-Path $root 'artifacts'
$packageRoot = Join-Path $artifactsDir 'package'
$packageDir = Join-Path $packageRoot 'GBFR.ChatOverlay'
$zipPath = Join-Path $artifactsDir "GBFR.ChatOverlay-$Version-Relink-2.0.4.zip"
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
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

try {
    & dotnet publish $projectPath `
        -c Release `
        --self-contained false `
        -o $packageDir `
        -p:OutputPath="$tempManaged" `
        -p:ReloadedILLink=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    Get-ChildItem -LiteralPath $packageDir -Recurse -File |
        Where-Object { $_.Extension -in '.pdb', '.xml' } |
        Remove-Item -Force

    $releaseDocuments = @{
        'README.md' = 'README.md'
        'docs/CHAT_BRIDGE.md' = 'CHAT_BRIDGE.md'
        'docs/SMOKE_TEST.md' = 'SMOKE_TEST.md'
        'docs/SESSION_HANDOFF_2026-07-25.md' = 'SESSION_HANDOFF_2026-07-25.md'
        'docs/VOICE_TRANSPORT.md' = 'VOICE_TRANSPORT.md'
        'docs/VOICE_TROUBLESHOOTING_MATRIX.md' = 'VOICE_TROUBLESHOOTING_MATRIX.md'
    }
    foreach ($entry in $releaseDocuments.GetEnumerator()) {
        $sourcePath = Join-Path $root $entry.Key
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Required release document was not found: $sourcePath"
        }
        Copy-Item -LiteralPath $sourcePath -Destination (Join-Path $packageDir $entry.Value) -Force
    }

    foreach ($requiredFile in @(
        'GBFR.ChatOverlay.dll',
        'GBFR.ChatOverlay.Native.dll',
        'GBFR.ChatOverlay.ConfiguratorUI.dll',
        'GBFR.OverlayHub.Contracts.dll',
        'ModConfig.json'
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

    $nativeBridgePath = Join-Path $packageDir 'GBFR.ChatOverlay.Native.dll'
    $nativeMachine = Get-PeMachine -Path $nativeBridgePath
    if ($nativeMachine -ne 0x8664) {
        throw ('Native bridge is not x64 (machine=0x{0:X4}): {1}' -f $nativeMachine, $nativeBridgePath)
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $packageRoot,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    $zipItem = Get-Item -LiteralPath $zipPath
    if ($zipItem.Length -le 0) {
        throw "ZIP was created but is empty: $zipPath"
    }

    Write-Output "ZIP: $zipPath"
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

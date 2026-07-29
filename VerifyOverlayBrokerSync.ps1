param(
    [Parameter(Mandatory = $true)]
    [string]$OtherRepository
)

$repositoryRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$otherRoot = [System.IO.Path]::GetFullPath($OtherRepository)
$sharedFiles = @(
    'GBFR.OverlayHub.Contracts/OverlayBroker.cs',
    'GBFR.OverlayHub.Contracts/OverlayBrokerCapabilities.cs',
    'GBFR.OverlayHub.Contracts/OverlayHubContracts.cs',
    'OverlayBroker/OverlayBrokerElection.cs',
    'OverlayBroker/OverlayBrokerHost.cs',
    'OverlayBroker/OverlayWindowInputClassifier.cs',
    'OverlayBroker/SharedImguiGraphicsBinding.cs'
)

$differences = @()
foreach ($relativePath in $sharedFiles) {
    $localPath = Join-Path $repositoryRoot $relativePath
    $otherPath = Join-Path $otherRoot $relativePath
    if (-not (Test-Path -LiteralPath $localPath) -or -not (Test-Path -LiteralPath $otherPath)) {
        $differences += "$relativePath (missing)"
        continue
    }

    $localHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $localPath).Hash
    $otherHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $otherPath).Hash
    if ($localHash -ne $otherHash) {
        $differences += "$relativePath (different)"
    }
}

if ($differences.Count -ne 0) {
    Write-Error ("Overlay Broker shared files are out of sync:`n" + ($differences -join "`n"))
    exit 1
}

Write-Host "Overlay Broker shared files match $otherRoot"

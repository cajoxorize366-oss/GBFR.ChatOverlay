# Set Working Directory
Split-Path $MyInvocation.MyCommand.Path | Push-Location
[Environment]::CurrentDirectory = $PWD

Remove-Item "$env:RELOADEDIIMODS/GBFR.ChatOverlay/*" -Force -Recurse
dotnet publish "./GBFR.ChatOverlay.csproj" -c Release -o "$env:RELOADEDIIMODS/GBFR.ChatOverlay" /p:OutputPath="./bin/Release" /p:ReloadedILLink="true"

# Restore Working Directory
Pop-Location
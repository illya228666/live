[CmdletBinding()]
param([switch]$Headless)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if ($Headless) {
    Invoke-Dotnet build tests/ArtificialLife.Tests -c Release
    Invoke-Dotnet build src/ArtificialLife.Cli -c Release
}
else { Invoke-Dotnet build ArtificialLife.sln -c Release }

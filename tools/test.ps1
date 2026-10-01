[CmdletBinding()]
param([switch]$Fast)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if ($Fast) { Invoke-Dotnet test tests/ArtificialLife.Tests -c Release --filter 'Category!=Learning' --verbosity minimal }
else { Invoke-Dotnet test tests/ArtificialLife.Tests -c Release --verbosity minimal }

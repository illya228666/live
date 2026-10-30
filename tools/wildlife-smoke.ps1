[CmdletBinding()]
param([switch]$Headless)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
& (Join-Path $RepoRoot 'tests/ArtificialLife.Tests/bin/Release/net10.0/ArtificialLife.Tests.exe') -noLogo -class ArtificialLife.Tests.WildlifeTests
if ($LASTEXITCODE -ne 0) { throw 'Wildlife checks failed.' }
& (Join-Path $PSScriptRoot 'observation-smoke.ps1') -Capture:(!$Headless)

[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
& (Join-Path $RepoRoot 'tests/ArtificialLife.Tests/bin/Release/net10.0/ArtificialLife.Tests.exe') -noLogo -class ArtificialLife.Tests.RuntimeTests -class ArtificialLife.Tests.ObservationTests -class ArtificialLife.Tests.ObservationLearningTests
if ($LASTEXITCODE -ne 0) { throw 'Runtime checks failed.' }
& (Join-Path $PSScriptRoot 'world-smoke.ps1')
& (Join-Path $PSScriptRoot 'observation-smoke.ps1') -Capture
& (Join-Path $PSScriptRoot 'visual-smoke.ps1')

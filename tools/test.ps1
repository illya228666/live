[CmdletBinding()]
param([switch]$Fast)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
& (Join-Path $PSScriptRoot 'package.ps1') -Headless
& (Join-Path $PSScriptRoot 'package-ecology.ps1')
& (Join-Path $PSScriptRoot 'package-workbench.ps1')
& (Join-Path $PSScriptRoot 'package-signal-diagnostic.ps1')
$env:LIVE_WORLD_PACKAGE_ROOT = Join-Path $RepoRoot ('artifacts/test-world-packages-' + [Guid]::NewGuid().ToString('N'))
& (Join-Path $PSScriptRoot 'package.ps1') -Version '1.1.0' -OutputRoot $env:LIVE_WORLD_PACKAGE_ROOT
Invoke-Dotnet build tests/ArtificialLife.Tests -c Release --verbosity minimal
$runner = Join-Path $RepoRoot 'tests/ArtificialLife.Tests/bin/Release/net10.0/ArtificialLife.Tests.exe'
if ($Fast) { & $runner -noLogo -trait- 'Category=Learning' }
else { & $runner -noLogo }
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }

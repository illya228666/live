[CmdletBinding()]
param([switch]$Fast)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
& (Join-Path $PSScriptRoot 'package.ps1') -Headless
& (Join-Path $PSScriptRoot 'package-ecology.ps1')
& (Join-Path $PSScriptRoot 'package-workbench.ps1')
Invoke-Dotnet build tests/ArtificialLife.Tests -c Release --verbosity minimal
$runner = Join-Path $RepoRoot 'tests/ArtificialLife.Tests/bin/Release/net10.0/ArtificialLife.Tests.exe'
if ($Fast) { & $runner -noLogo -trait- 'Category=Learning' }
else { & $runner -noLogo }
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }

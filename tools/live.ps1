[CmdletBinding()]
param([string]$Config='configs/runtime.json', [long]$Steps=0, [string]$Checkpoint='', [string]$Trajectory='')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if (!(Test-Path (Join-Path $RepoRoot 'artifacts/packages/movement/1.0.0/module.json'))) { throw 'Prepare packages explicitly with tools/package.ps1 before launch.' }
Invoke-Dotnet build src/ArtificialLife.Cli -c Release --verbosity quiet
$liveArguments = @('run','--project','src/ArtificialLife.Cli','-c','Release','--no-build','--','live','--config',$Config)
if ($Steps -gt 0) { $liveArguments += @('--steps', "$Steps") }
if ($Checkpoint) { $liveArguments += @('--checkpoint', $Checkpoint) }
if ($Trajectory) { $liveArguments += @('--trajectory', $Trajectory) }
Invoke-Dotnet @liveArguments

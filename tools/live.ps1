[CmdletBinding()]
param([string]$Config='configs/runtime.json', [long]$Steps=0, [string]$Checkpoint='', [string]$Trajectory='')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
& (Join-Path $PSScriptRoot 'package.ps1') -Headless
Invoke-Dotnet build src/ArtificialLife.Cli -c Release --verbosity quiet
$liveArguments = @('run','--project','src/ArtificialLife.Cli','-c','Release','--no-build','--','live','--config',$Config)
if ($Steps -gt 0) { $liveArguments += @('--steps', "$Steps") }
if ($Checkpoint) { $liveArguments += @('--checkpoint', $Checkpoint) }
if ($Trajectory) { $liveArguments += @('--trajectory', $Trajectory) }
Invoke-Dotnet @liveArguments

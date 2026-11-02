[CmdletBinding()]
param([string]$Config='configs/runtime.json', [long]$Steps=0, [string]$Checkpoint='', [string]$Trajectory='')
$arguments=@('--mode','learning','--config',[IO.Path]::GetFullPath($Config),'--directory',('artifacts/worlds/live-'+[Guid]::NewGuid().ToString('N')))
if ($Steps -gt 0) { $arguments+=@('--steps',"$Steps") }
if ($Checkpoint) { $arguments+=@('--checkpoint',$Checkpoint) }
if ($Trajectory) { $arguments+=@('--trajectory',$Trajectory) }
& (Join-Path $PSScriptRoot 'world.ps1') run @arguments

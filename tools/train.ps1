[CmdletBinding()]
param([string]$Config = 'configs/default.json', [int]$Steps = 0, [string]$Checkpoint = 'artifacts/checkpoints/thermal')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$cliArgs = @('run', '--project', 'src/ArtificialLife.Cli', '-c', 'Release', '--', 'train', '--config', $Config, '--checkpoint', $Checkpoint)
if ($Steps -gt 0) { $cliArgs += @('--steps', "$Steps") }
Invoke-Dotnet @cliArgs

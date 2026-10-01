[CmdletBinding()]
param([string]$Checkpoint = 'artifacts/checkpoints/thermal')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release -- evaluate --checkpoint $Checkpoint

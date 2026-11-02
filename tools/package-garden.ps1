[CmdletBinding()]
param([string]$Mechanic='flora', [string]$Version='1.0.0', [string]$OutputRoot='artifacts/packages', [switch]$NoBuild, [string]$Cli='')
& (Join-Path $PSScriptRoot 'package-module.ps1') -Description (Join-Path $PSScriptRoot '../src/ArtificialLife.Modules.Garden/package.json') -Id $Mechanic -Version $Version -OutputRoot $OutputRoot -NoBuild:$NoBuild -Cli $Cli

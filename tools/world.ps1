[CmdletBinding()]
param([Parameter(Position=0,Mandatory=$true)][ValidateSet('run','status','snapshot','checkpoint','update','rollback','screenshot','stop','help')][string]$Action, [Parameter(ValueFromRemainingArguments=$true)][string[]]$WorldArguments)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$worldCli = Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
if (!(Test-Path -LiteralPath $worldCli)) { throw 'Build src/ArtificialLife.Cli in Release before starting a world.' }
Push-Location $RepoRoot
try { & $worldCli world $Action @WorldArguments; if ($LASTEXITCODE -ne 0) { throw ('World command failed: ' + $LASTEXITCODE) } }
finally { Pop-Location }

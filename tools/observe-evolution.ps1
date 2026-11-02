[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Label,
      [string]$Directory='artifacts/evolution-20261102/world',
      [string]$Output='artifacts/evolution-20261102')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if ($Label -notmatch '^[A-Za-z0-9_-]+$') { throw 'Invalid observation label.' }
$evolutionCli = Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
$evolutionSession = (Get-Content (Join-Path $Directory 'session-id') -Raw).Trim()
$evolutionJson = & $evolutionCli world snapshot --session $evolutionSession
if ($LASTEXITCODE -ne 0) { throw 'Snapshot failed.' }
$evolutionSnapshot = $evolutionJson | ConvertFrom-Json
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$evolutionJson | Set-Content -LiteralPath (Join-Path $Output ($Label + '.json')) -Encoding UTF8
$evolutionFrame = $evolutionSnapshot.state.frame
[ordered]@{label=$Label;age=$evolutionFrame.age;time=$evolutionFrame.time;revision=$evolutionFrame.revision;
    entities=@($evolutionFrame.entities).Count;metrics=$evolutionSnapshot.state.metrics;fault=$evolutionSnapshot.state.fault} | ConvertTo-Json -Depth 10

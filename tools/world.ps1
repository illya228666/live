[CmdletBinding()]
param([Parameter(Position=0,Mandatory=$true)][ValidateSet('run','status','snapshot','checkpoint','update','rollback','screenshot','stop','help','list','pause','resume','speed','reset','new-life','evaluation')][string]$Action, [string]$HostRelease='', [Parameter(ValueFromRemainingArguments=$true)][string[]]$WorldArguments)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$worldCli=$null
if ($Action -eq 'run') {
    $directoryIndex=[Array]::IndexOf($WorldArguments,'--directory')
    $worldDirectory=if ($directoryIndex -ge 0) { [IO.Path]::GetFullPath($WorldArguments[$directoryIndex+1]) } else { Join-Path $RepoRoot 'artifacts/worlds/default' }
    $releaseRecord=Join-Path $worldDirectory 'host-release.json'
    if (!$HostRelease -and (Test-Path -LiteralPath $releaseRecord)) { $worldCli=Get-LiveCli (Get-Content -LiteralPath $releaseRecord -Raw | ConvertFrom-Json).cli -Pinned }
    # Supervisor records the actual executable/PID only after acquiring exclusive ownership.
}
if (!$worldCli) { $worldCli = if ($HostRelease) { Get-LiveCli (Join-Path ([IO.Path]::GetFullPath($HostRelease)) 'ArtificialLife.Cli.exe') -Pinned:($Action -eq 'run') } else { Get-LiveCli -Pinned:($Action -eq 'run') } }
Push-Location $RepoRoot
try { & $worldCli world $Action @WorldArguments; if ($LASTEXITCODE -ne 0) { throw ('World command failed: ' + $LASTEXITCODE) } }
finally { Pop-Location }

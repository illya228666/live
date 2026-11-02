[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('publish','start','switch')][string]$Action,
      [string]$Release='', [string]$Name='', [string]$Directory='', [string]$Restore='',
      [string]$Config='configs/world-only.json', [string]$Speed='10x', [switch]$NoBuild,
      [int]$TimeoutSeconds=60)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if ($Action -eq 'publish') {
    if (!$Name) { $Name=(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8) }
    if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid Host release name.' }
    $destination=Join-Path $RepoRoot ('artifacts/hosts/'+$Name)
    if (Test-Path -LiteralPath $destination) { throw 'Host releases are immutable. Choose a new name.' }
    $stage=Join-Path $RepoRoot ('artifacts/hosts/.staging-'+[Guid]::NewGuid().ToString('N'))
    $log=Join-Path $RepoRoot ('artifacts/hosts/'+$Name+'.build.log')
    New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
    $buildArgs=@('publish','src/ArtificialLife.Cli','-c','Release','--no-restore','-o',$stage,'--verbosity','quiet')
    if ($NoBuild) { $buildArgs+='--no-build' }
    & $script:Dotnet @buildArgs *> $log
    if ($LASTEXITCODE -ne 0) { throw ('Host build failed: '+$log) }
    $hashes=@{}
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $hashes[[IO.Path]::GetRelativePath($stage,$file.FullName)]=(Get-FileHash -LiteralPath $file.FullName).Hash
    }
    Write-LiveJson (Join-Path $stage 'host.json') @{revision=(& git -C $RepoRoot rev-parse HEAD);files=$hashes}
    [IO.Directory]::Move($stage,$destination)
    $cli=Join-Path $destination 'ArtificialLife.Cli.exe'
    Write-LiveJson (Join-Path $RepoRoot 'artifacts/hosts/current.json') @{cli=$cli}
    [ordered]@{status='passed';release=$destination;cli=$cli;log=$log} | ConvertTo-Json -Compress
    return
}
if (!$Directory) { throw 'Choose one world with -Directory.' }
$Directory=[IO.Path]::GetFullPath($Directory)
$record=Join-Path $Directory 'host-release.json'
$cli=if ($Release) { Get-LiveCli (Join-Path ([IO.Path]::GetFullPath($Release)) 'ArtificialLife.Cli.exe') -Pinned }
    elseif ($Action -eq 'start' -and (Test-Path -LiteralPath $record)) { Get-LiveCli (Get-Content -LiteralPath $record -Raw | ConvertFrom-Json).cli -Pinned }
    else { Get-LiveCli -Pinned }
if ($Action -eq 'start') {
    $arguments=@('--speed',$Speed)
    if ($Restore) { $arguments+=@('--restore',[IO.Path]::GetFullPath($Restore)) }
    elseif (!(Test-Path -LiteralPath (Join-Path $Directory 'recovery.json'))) { $arguments+=@('--config',[IO.Path]::GetFullPath($Config)) }
    $process=Start-LiveHost $cli $Directory $arguments
    $snapshot=Wait-LiveHost $cli $Directory $process $TimeoutSeconds
    Write-LiveJson (Join-Path $Directory 'host-release.json') @{cli=$cli;processId=$process.Id}
    [ordered]@{status='running';session=$snapshot.session.sessionId;age=$snapshot.state.frame.age;revision=$snapshot.state.frame.revision;cli=$cli;processId=$process.Id} | ConvertTo-Json -Compress
    return
}
# This transition is intentionally World Only: physical checkpoints omit optimizer/replay.
$session=(Get-Content -LiteralPath (Join-Path $Directory 'session-id') -Raw).Trim()
$sessions=@(Invoke-LiveJson $cli @('world','list'))
$selected=@($sessions | Where-Object SessionId -EQ $session)
if ($selected.Count -ne 1 -or $selected[0].Mode -ne 'WorldOnly') { throw 'Host switch requires an active World Only session; Learning optimizer/replay cannot be restored.' }
$pending=Get-Content -LiteralPath (Join-Path $Directory 'recovery.json') -Raw | ConvertFrom-Json
if ($pending.Pending -or @(Get-ChildItem (Join-Path $Directory 'commands') -Filter '*.ready.json').Count) { throw 'Wait for pending protected commands/probation before switching Host.' }
$oldHost=Get-Content -LiteralPath (Join-Path $Directory 'host-release.json') -Raw | ConvertFrom-Json
$before=Get-LiveSnapshot $cli $session
if ($before.state.fault) { throw 'Refusing to switch a faulted world.' }
Invoke-LiveJson $cli @('world','pause','--session',$session) | Out-Null
$switchId='host-switch-'+[Guid]::NewGuid().ToString('N')
$work=Join-Path $Directory ('host-switches/'+$switchId)
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    # The observation owner services this synchronous save even while paused.
    Invoke-LiveJson $cli @('world','checkpoint','--session',$session,'--target',(Join-Path $work 'checkpoint'),'--id',($switchId+'-save')) | Out-Null
} catch {
    Invoke-LiveJson $cli @('world','resume','--session',$session) | Out-Null
    throw
}
$pointer=Get-Content -LiteralPath (Join-Path $work 'checkpoint/current') -Raw | ConvertFrom-Json
$savedGeneration=Join-Path $work ('checkpoint/'+$pointer.Generation)
$savedWorld=Get-Content -LiteralPath (Join-Path $savedGeneration 'world.json') -Raw | ConvertFrom-Json
$saved=@{Age=$savedWorld.World.Age;Revision=$savedWorld.Revision}
try {
    & (Join-Path $PSScriptRoot 'check-evolution.ps1') -Checkpoint (Join-Path $work 'checkpoint') -Output (Join-Path $work 'preflight') -Cli $cli *> (Join-Path $work 'preflight.log')
} catch {
    Invoke-LiveJson $oldHost.cli @('world','resume','--session',$session) | Out-Null
    throw
}
$recovery=Get-Content -LiteralPath (Join-Path $Directory 'recovery.json') -Raw | ConvertFrom-Json -AsHashtable
$recovery.Current=$savedGeneration
Write-LiveJson (Join-Path $work 'recovery.json') $recovery
& $cli world stop --directory $Directory --id ($switchId+'-stop') | Out-Null
$stopped=Wait-LiveResult $Directory ($switchId+'-stop') $TimeoutSeconds
if (!$stopped.Accepted) { throw $stopped.Message }
$oldProcessId=if ($oldHost.PSObject.Properties.Name -contains 'processId') { $oldHost.processId } else {
    $heartbeat=Get-Content -LiteralPath (Join-Path $Directory 'heartbeat.json') -Raw | ConvertFrom-Json
    (Get-CimInstance Win32_Process -Filter "ProcessId=$($heartbeat.ProcessId)").ParentProcessId
}
if (Get-Process -Id $oldProcessId -ErrorAction SilentlyContinue) { Wait-Process -Id $oldProcessId -Timeout $TimeoutSeconds -ErrorAction Stop }
$replacement=$null
try {
    $replacement=Start-LiveHost $cli $Directory @('--speed',$Speed)
    $after=Wait-LiveHost $cli $Directory $replacement $TimeoutSeconds
    if ($after.state.frame.age -lt $saved.Age -or $after.state.frame.revision -ne $saved.Revision -or $after.state.fault) { throw 'Replacement Host failed world continuity checks.' }
    Write-LiveJson (Join-Path $Directory 'host-release.json') @{cli=$cli;processId=$replacement.Id}
    [ordered]@{status='switched';session=$session;beforeAge=$saved.Age;age=$after.state.frame.age;revision=$after.state.frame.revision;previousCli=$oldHost.cli;cli=$cli;checkpoint=(Join-Path $work 'checkpoint')} | ConvertTo-Json -Compress
} catch {
    $failure=$_.Exception.Message
    if ($replacement -and !$replacement.HasExited) {
        & $cli world stop --directory $Directory --id ($switchId+'-failed-stop') | Out-Null
        if (!$replacement.WaitForExit($TimeoutSeconds*1000)) { throw 'Replacement did not stop; recovery left untouched. Stop it before retrying.' }
    }
    Copy-Item -LiteralPath (Join-Path $work 'recovery.json') -Destination (Join-Path $Directory 'recovery.json') -Force
    $previous=Start-LiveHost $oldHost.cli $Directory @('--speed',$Speed)
    $null=Wait-LiveHost $oldHost.cli $Directory $previous $TimeoutSeconds
    Write-LiveJson (Join-Path $Directory 'host-release.json') @{cli=$oldHost.cli;processId=$previous.Id}
    throw ('Host switch failed; previous release restored: '+$failure)
}

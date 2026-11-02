[CmdletBinding()]
param([string]$Checkpoint='artifacts/evolution-20261102/checkpoint-2',
      [string]$Directory='artifacts/ev-rollback',
      [string]$Package='artifacts/evolution-20261102/packages/pollination/1.0.0')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if (Test-Path -LiteralPath $Directory) { throw 'Use a new rollback smoke directory.' }
$rollbackRoot = [IO.Path]::GetFullPath($Directory)
New-Item -ItemType Directory -Path $rollbackRoot | Out-Null
$rollbackCli = Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
$rollbackStart = [Diagnostics.ProcessStartInfo]::new($rollbackCli)
$rollbackStart.UseShellExecute = $false
$rollbackStart.CreateNoWindow = $true
$rollbackStart.WindowStyle = 'Hidden'
$rollbackStart.WorkingDirectory = $RepoRoot
foreach ($argument in @('world','run','--restore',[IO.Path]::GetFullPath($Checkpoint),'--directory',$rollbackRoot,'--speed','100x','--checkpoint-seconds','3600','--probation-steps','100')) { $rollbackStart.ArgumentList.Add($argument) }
$rollbackHost = [Diagnostics.Process]::Start($rollbackStart)
function Wait-RollbackFile([string]$Path) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $Path)) {
        if ($rollbackHost.HasExited -or $watch.Elapsed.TotalSeconds -gt 40) { throw ('Timed out waiting for ' + $Path) }
        Start-Sleep -Milliseconds 200
    }
}
try {
    Wait-RollbackFile (Join-Path $rollbackRoot 'heartbeat.json')
    $rollbackSession = (Get-Content (Join-Path $rollbackRoot 'session-id') -Raw).Trim()
    & $rollbackCli world update --directory $rollbackRoot --sources ([IO.Path]::GetFullPath($Package)) --id install
    Wait-RollbackFile (Join-Path $rollbackRoot 'commands/install.result.json')
    $rollbackInstall = Get-Content (Join-Path $rollbackRoot 'commands/install.result.json') -Raw | ConvertFrom-Json
    if (!$rollbackInstall.Accepted) { throw $rollbackInstall.Message }
    $rollbackWait = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 200
        $rollbackRecovery = Get-Content (Join-Path $rollbackRoot 'recovery.json') -Raw | ConvertFrom-Json
        if ($rollbackWait.Elapsed.TotalSeconds -gt 40) { throw 'Probation did not finish.' }
    } while ($rollbackRecovery.Pending)
    $rollbackInstalled = & $rollbackCli world snapshot --session $rollbackSession | ConvertFrom-Json
    if (@($rollbackInstalled.state.frame.entities | Where-Object { $_.components.PSObject.Properties.Name -contains 'pollination.moth' }).Count -ne 6) { throw 'Moths did not activate.' }
    & $rollbackCli world rollback --directory $rollbackRoot --id revert
    Wait-RollbackFile (Join-Path $rollbackRoot 'commands/revert.result.json')
    $rollbackResult = Get-Content (Join-Path $rollbackRoot 'commands/revert.result.json') -Raw | ConvertFrom-Json
    if (!$rollbackResult.Accepted) { throw $rollbackResult.Message }
    $rollbackWatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 300
        $rollbackRestored = & $rollbackCli world snapshot --session $rollbackSession 2>$null | ConvertFrom-Json
        if ($rollbackWatch.Elapsed.TotalSeconds -gt 30) { throw 'Rollback worker did not reconnect.' }
    } while (!$rollbackRestored -or $rollbackRestored.state.frame.revision -ne 3)
    $rollbackBaseline = Get-Content (Join-Path $rollbackResult.Artifact 'world.json') -Raw | ConvertFrom-Json
    $rollbackFrame = $rollbackRestored.state.frame
    if (@($rollbackFrame.entities | Where-Object { $_.components.PSObject.Properties.Name -contains 'pollination.moth' }).Count -ne 0) { throw 'Rollback retained the new module entities.' }
    foreach ($entity in $rollbackBaseline.World.Entities) {
        if (@($rollbackFrame.entities | Where-Object { $_.id -eq $entity.Id }).Count -ne 1) { throw 'Rollback lost an existing entity.' }
    }
    if ($rollbackFrame.age -lt $rollbackBaseline.World.Age) { throw 'Rollback did not restore the recorded clock.' }
    [ordered]@{passed=$true;installedMoths=6;restoredEntities=@($rollbackFrame.entities).Count;restoredRevision=$rollbackFrame.revision;
        sourceAge=$rollbackBaseline.World.Age;observedAge=$rollbackFrame.age;checkpoint=$rollbackResult.Artifact} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $rollbackRoot 'summary.json') -Encoding UTF8
    Write-Output ('ROLLBACK_OK ' + $rollbackRoot)
}
finally {
    if (!$rollbackHost.HasExited) {
        & $rollbackCli world stop --directory $rollbackRoot --id stop
        if (!$rollbackHost.WaitForExit(10000)) { $rollbackHost.Kill($true) }
    }
    $rollbackHost.Dispose()
}

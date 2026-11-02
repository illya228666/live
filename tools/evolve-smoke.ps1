[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Checkpoint,
      [string]$Output=('artifacts/evolve-smoke-'+[Guid]::NewGuid().ToString('N').Substring(0,8)), [switch]$NoBuild)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$root=[IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $root) { throw 'Use a new isolated smoke directory.' }
New-Item -ItemType Directory -Path $root | Out-Null
$tag='smoke-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
function Assert-Smoke([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message } }
function Invoke-SmokeIteration([hashtable]$Options) {
    $json=& (Join-Path $PSScriptRoot 'evolve.ps1') @Options
    return ($json -join "`n") | ConvertFrom-Json -Depth 50
}
$world=Join-Path $root 'world'
try {
    $first=(& (Join-Path $PSScriptRoot 'host.ps1') -Action publish -Name ($tag+'-a') -NoBuild:$NoBuild) | ConvertFrom-Json
    $retrySource=Join-Path $root 'candidate-checkpoint'
    $checkOptions=@{Action='check';Description='src/ArtificialLife.Modules.Garden/package.json';PackageId='flora';Version=$tag;PackageRoot=(Join-Path $root 'packages');Checkpoint=$retrySource;Output=(Join-Path $root 'check');NoBuild=$NoBuild}
    $interrupted=Invoke-SmokeIteration $checkOptions
    Assert-Smoke ($interrupted.status -eq 'failed' -and $interrupted.errorStage -eq 'validation') 'Missing partial check failure.'
    $testCompleted=(Get-Item -LiteralPath $interrupted.checks.test.log).LastWriteTimeUtc
    $generation=[IO.Path]::GetFullPath($Checkpoint)
    if (Test-Path -LiteralPath (Join-Path $generation 'current')) {
        $pointer=Get-Content -LiteralPath (Join-Path $generation 'current') -Raw | ConvertFrom-Json
        $generation=Join-Path $generation $pointer.Generation
    }
    Copy-Item -LiteralPath $generation -Destination $retrySource -Recurse
    $check=Invoke-SmokeIteration $checkOptions
    Assert-Smoke ((Get-Item -LiteralPath $check.checks.test.log).LastWriteTimeUtc -eq $testCompleted) 'Partial retry repeated successful tests.'
    Assert-Smoke ($check.status -eq 'passed') ('Candidate failed: '+($check.errors -join ';'))
    $package=$check.artifacts.packages[0]
    $start=(& (Join-Path $PSScriptRoot 'host.ps1') -Action start -Directory $world -Release $first.release -Restore $Checkpoint -Speed 100x) | ConvertFrom-Json
    $process=Get-Process -Id $start.processId
    $hashBefore=(Get-FileHash -LiteralPath $first.cli).Hash
    Invoke-Dotnet build src/ArtificialLife.Cli -c Release --no-restore --verbosity quiet *> (Join-Path $root 'dev-build.log')
    Assert-Smoke (!$process.HasExited -and (Get-FileHash -LiteralPath $first.cli).Hash -eq $hashBefore) 'Dev build changed/stopped Running Host.'
    $heartbeat=Get-Content -LiteralPath (Join-Path $world 'heartbeat.json') -Raw | ConvertFrom-Json
    Assert-Smoke ((Get-Process -Id $heartbeat.ProcessId).Path.StartsWith($first.release+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'Worker uses dev binaries.'

    $corrupt=Join-Path $root 'corrupt'
    Copy-Item -LiteralPath $package -Destination $corrupt -Recurse
    $manifest=Get-Content -LiteralPath (Join-Path $corrupt 'module.json') -Raw | ConvertFrom-Json
    Add-Content -LiteralPath (Join-Path $corrupt $manifest.assembly) -Value 'intentional test corruption'
    $bad=Invoke-SmokeIteration @{Action='check';Sources=@($corrupt);NoBuild=$true;NoTests=$true;Output=(Join-Path $root 'bad-package')}
    Assert-Smoke ($bad.status -eq 'failed' -and $bad.errorStage -eq 'package') 'Missing package error classification.'
    $invalid=Join-Path $root 'invalid'
    Copy-Item -LiteralPath $package -Destination $invalid -Recurse
    $manifest=Get-Content -LiteralPath (Join-Path $invalid 'module.json') -Raw | ConvertFrom-Json -AsHashtable
    $manifest.version=$tag+'-invalid'; $manifest.requires+=@('infrastructure-missing-capability')
    Write-LiveJson (Join-Path $invalid 'module.json') $manifest
    $bad=Invoke-SmokeIteration @{Action='deploy';Directory=$world;Sources=@($invalid);Output=(Join-Path $root 'rejected')}
    Assert-Smoke ($bad.status -eq 'failed' -and $bad.errorStage -eq 'validation') 'Missing isolated validation rejection.'

    $options=@{Action='deploy';Directory=$world;Sources=@($package);Output=(Join-Path $root 'deploy')}
    $deployed=Invoke-SmokeIteration $options
    Assert-Smoke ($deployed.status -eq 'passed' -and $deployed.deployment -eq 'committed') ('Deploy failed: '+($deployed.errors -join ';'))
    Assert-Smoke (!$process.HasExited) 'Module activation restarted Host.'
    $capture=Get-Content -LiteralPath ($deployed.artifacts.capture+'.json') -Raw | ConvertFrom-Json
    Assert-Smoke ($capture.sessionId -eq $start.session -and $capture.revision -eq $deployed.world.revision) 'Wrong Viewer frame.'
    $replay=Invoke-SmokeIteration $options
    Assert-Smoke ($replay.id -eq $deployed.id) 'Retry did not return the original iteration.'
    & (Join-Path $PSScriptRoot 'check-evolution.ps1') -Checkpoint $deployed.artifacts.checkpoint -Output (Join-Path $root 'restore-check') *> (Join-Path $root 'restore.log')
    $second=(& (Join-Path $PSScriptRoot 'host.ps1') -Action publish -Name ($tag+'-b') -NoBuild) | ConvertFrom-Json
    $switched=(& (Join-Path $PSScriptRoot 'host.ps1') -Action switch -Directory $world -Release $second.release -Speed 100x) | ConvertFrom-Json
    Assert-Smoke ($switched.session -eq $start.session -and $switched.age -ge $switched.beforeAge -and $switched.revision -eq $deployed.world.revision) 'Switch lost world continuity.'
    $observed=Invoke-SmokeIteration @{Action='observe';Directory=$world;Capture=$true;Save=$true;Output=(Join-Path $root 'after-switch')}
    Assert-Smoke ($observed.status -eq 'passed' -and $observed.world.ageDelta -gt 0) 'Switched world is not observable/advancing.'
    $summary=[ordered]@{status='passed';session=$start.session;sourceCheckpoint=[IO.Path]::GetFullPath($Checkpoint);candidate=$package;deployment=$deployed.artifacts.result;observation=$observed.artifacts.result;switch=$switched;checks=@('package-integrity','error-stage','validation-rejection','probation-commit','host-isolation','dev-build-while-running','retry','partial-check-retry','checkpoint-continuation','host-switch','godot-capture')}
    Write-LiveJson (Join-Path $root 'summary.json') $summary
    $summary | ConvertTo-Json -Depth 8 -Compress
} finally {
    if (Test-Path -LiteralPath (Join-Path $world 'host-release.json')) {
        $record=Get-Content -LiteralPath (Join-Path $world 'host-release.json') -Raw | ConvertFrom-Json
        if (Get-Process -Id $record.processId -ErrorAction SilentlyContinue) {
            & $record.cli world stop --directory $world --id ($tag+'-stop') | Out-Null
            $null=Wait-LiveResult $world ($tag+'-stop') 45
            if (Get-Process -Id $record.processId -ErrorAction SilentlyContinue) { Wait-Process -Id $record.processId -Timeout 30 }
        }
    }
}

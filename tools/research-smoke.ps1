[CmdletBinding()]
param([string]$Campaign='infrastructure-smoke',[string]$Study=('smoke-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')),[double]$WorkerMemoryGiB=0)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$driver=Join-Path $PSScriptRoot 'research.ps1'
$currentBranch=(& git -C $RepoRoot branch --show-current).Trim()
& $driver -Action init -Campaign $Campaign -Branch $currentBranch | Out-Null
if ($WorkerMemoryGiB -gt 0) {
    $settingsPath=Join-Path $RepoRoot ("research/campaigns/$Campaign/campaign.json")
    $settings=Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json -AsHashtable
    $settings.workerMemoryEstimateGiB=$WorkerMemoryGiB
    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding utf8
}
$registration=Join-Path $RepoRoot ("research/campaigns/$Campaign/studies/$Study.json")
if (!(Test-Path -LiteralPath $registration)) {
    & $driver -Action add -Campaign $Campaign -Id $Study -Config (Join-Path $RepoRoot 'configs/research-smoke.json') -Hypothesis (Join-Path $RepoRoot 'research/smoke-hypothesis.json') -Stage diagnostic -DecisionRule 'Identical action hashes/counters/behavior within 1e-9 for sequential versus parallel execution; retained completed trials after stop/resume; context recovers conclusion and next step.' | Out-Null
}
$entry=Get-Content -LiteralPath $registration -Raw | ConvertFrom-Json
$studyRoot=Join-Path $RepoRoot ("artifacts/research/$Campaign/studies/$Study")
$executor=Join-Path (Join-Path $RepoRoot $entry.bundle) 'ArtificialLife.Cli.exe'
$spec=Join-Path $studyRoot 'config.json'
$sequential=Join-Path $studyRoot 'sequential'
$stop=Join-Path $RepoRoot ("artifacts/research/$Campaign/STOP")
$suitePath=Join-Path (Join-Path $RepoRoot $entry.output) 'suite.json'
if ((Test-Path -LiteralPath $suitePath) -and (Get-Content $suitePath -Raw | ConvertFrom-Json).Status -eq 'completed') {
    $proofPath=Join-Path $studyRoot 'smoke-proof.json'
    if (Test-Path -LiteralPath $proofPath) {
        $savedProof=Get-Content $proofPath -Raw | ConvertFrom-Json -AsHashtable
        if ($savedProof.ContainsKey('parallel') -and $savedProof.parallel -ge 2 -and $savedProof.matches -eq $entry.plan.ExpectedRuns) {
            $savedProof | ConvertTo-Json; return
        }
    }
    throw 'Existing trials were already completed without a two-worker proof. Use a new study id; do not relabel old execution as parallel.'
}
$orphanKilled=$false
if (!(Test-Path -LiteralPath (Join-Path (Join-Path $RepoRoot $entry.output) 'suite.json'))) {
    & $driver -Action stop -Campaign $Campaign
    & $executor experiment --config $spec --output (Join-Path $RepoRoot $entry.output) --stop-file $stop
    if ($LASTEXITCODE -ne 130) { throw 'Expected paused preflight.' }
    $suite=Get-Content (Join-Path (Join-Path $RepoRoot $entry.output) 'suite.json') -Raw | ConvertFrom-Json
    $orphanDirectory=Join-Path (Join-Path $RepoRoot $entry.output) $suite.Runs[0]
    $start=[Diagnostics.ProcessStartInfo]::new($executor)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    foreach ($argument in @('experiment-worker','--job',(Join-Path $orphanDirectory 'job.json'),'--output',$orphanDirectory)) { $start.ArgumentList.Add($argument) }
    $orphan=[Diagnostics.Process]::Start($start)
    try {
        @{Id=$orphan.Id;StartedUtc=$orphan.StartTime.ToUniversalTime().ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $orphanDirectory 'process.json') -Encoding utf8
        $timer=[Diagnostics.Stopwatch]::StartNew();$owned=$false
        while (!$owned -and !$orphan.HasExited -and $timer.Elapsed.TotalSeconds -lt 15) {
            try { $probe=[IO.File]::Open((Join-Path $orphanDirectory 'worker.lock'),'OpenOrCreate','ReadWrite','None');$probe.Dispose() }
            catch [IO.IOException] { $owned=$true }
            if (!$owned) { Start-Sleep -Milliseconds 20 }
        }
        if (!$owned -or $orphan.HasExited) { throw 'Orphan simulation failed to acquire a live worker.' }
        & $driver -Action stop -Campaign $Campaign -Force
        if (!$orphan.WaitForExit(15000)) { throw 'Orphan force-stop failed.' }
        if ($orphan.ExitCode -eq 0) { throw 'Worker completed rather than being force-stopped.' }
        $orphanKilled=$true
    } finally { if (!$orphan.HasExited) { $orphan.Kill($true);$orphan.WaitForExit() };$orphan.Dispose() }
}
& $executor experiment --config $spec --output $sequential --parallel 1
if ($LASTEXITCODE -ne 0) { throw 'Sequential reference failed.' }
& $driver -Action stop -Campaign $Campaign
& $executor experiment --config $spec --output (Join-Path $RepoRoot $entry.output) --stop-file $stop
if ($LASTEXITCODE -ne 130 -and $LASTEXITCODE -ne 0) { throw 'Expected a paused or already completed study.' }
& $driver -Action note -Campaign $Campaign -Id $Study -Text 'Sequential reference saved. A deliberate stop request interrupted admission; immutable configs and completed evidence are retained.' -Next 'Resume the registered study and compare with the sequential reference.' | Out-Null
& $driver -Action run -Campaign $Campaign -Id $Study -Minutes 2 -Parallel 2 -Resume | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Campaign resume failed.' }
$execution=Get-Content (Join-Path $RepoRoot ("artifacts/research/$Campaign/last-execution.json")) -Raw | ConvertFrom-Json
if ($execution.parallel -lt 2) { throw 'This smoke requires room for two tiny workers. Retry with a justified -WorkerMemoryGiB estimate (0.5 GiB for this small fixed configuration), not by dropping the free-memory reserve.' }
$comparison=Join-Path $studyRoot 'reproducibility'
& $executor compare --inputs ($sequential + ',' + (Join-Path $RepoRoot $entry.output)) --output $comparison
if ($LASTEXITCODE -ne 0) { throw 'Reproducibility comparison failed.' }
$report=Get-Content (Join-Path $comparison 'comparison.json') -Raw | ConvertFrom-Json
if ($report.Reproductions.Count -ne $entry.plan.ExpectedRuns -or @($report.Reproductions | Where-Object { -not $_.Matches }).Count -gt 0) { throw 'Missing or differing repetitions.' }
$completed=@(Get-ChildItem -LiteralPath (Join-Path $RepoRoot $entry.output) -Filter result.json -Recurse | ForEach-Object { [pscustomobject]@{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;Written=$_.LastWriteTimeUtc} })
& $driver -Action note -Campaign $Campaign -Id $Study -Text "Methodological hypothesis supported in this smoke: $($report.Reproductions.Count) matching pairs at 1e-9. No conclusion about behavioral superiority or adequacy of thermoregulation scenarios." -Next 'Do not start a long campaign yet. On the next user request, read BASELINE.md, choose a research question, and preregister a discriminating scenario/pilot.' | Out-Null
# Memory changes between invocations must not invalidate completed computation.
& $driver -Action run -Campaign $Campaign -Id $Study -Minutes 1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Completed-study reconciliation failed.' }
foreach ($result in $completed) {
    if ((Get-FileHash -LiteralPath $result.Path -Algorithm SHA256).Hash -ne $result.Hash -or (Get-Item -LiteralPath $result.Path).LastWriteTimeUtc -ne $result.Written) { throw 'Completed result was rewritten.' }
}
$context=& $driver -Action context -Campaign $Campaign
if ($context -notmatch 'matching pairs' -or $context -notmatch 'Do not start a long campaign yet') { throw 'Context failed to recover analysis/next step.' }
$proof=@{schemaVersion=1;campaign=$Campaign;study=$Study;pairs=$report.Reproductions.Count;matches=@($report.Reproductions | Where-Object Matches).Count;parallel=$execution.parallel;orphanForceStopped=$orphanKilled;completedResultsPreserved=$true;contextRecovered=$true;comparison=[IO.Path]::GetRelativePath($RepoRoot,$comparison);recordedUtc=[DateTimeOffset]::UtcNow.ToString('o')}
$proof | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $studyRoot 'smoke-proof.json') -Encoding utf8
$proof | ConvertTo-Json

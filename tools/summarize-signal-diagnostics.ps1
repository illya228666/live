#requires -Version 7.2
[CmdletBinding()]
param([string]$Campaign='local', [string]$Output='signal-diagnostic-01-summary.json')
. (Join-Path $PSScriptRoot 'common.ps1')
$signalMemory = Join-Path $RepoRoot ('research/campaigns/' + $Campaign)
function Mean($Values) {
    $present = @($Values)
    if ($present.Count -eq 0 -or @($present | Where-Object {$null -eq $_}).Count -gt 0) { return $null }
    [double](($present | Measure-Object -Average).Average)
}
function Success($Run, [string]$Scenario, [long]$At) {
    Mean @($Run.Evaluations | Where-Object {$_.Kind -eq 'policy' -and $_.Scenario -eq $Scenario -and $_.AtStep -eq $At} |
        ForEach-Object {$_.Metrics.'signal.success'.Value})
}
function WeightHash([string]$Directory, [string]$Policy) {
    $root = Join-Path $Directory ('policies/' + $Policy)
    $pointer = Get-Content (Join-Path $root 'current') -Raw | ConvertFrom-Json -AsHashtable
    $generation = Join-Path $root $pointer.Generation
    $manifest = Get-Content (Join-Path $generation 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    $hash = (Get-FileHash (Join-Path $generation 'weights.bin') -Algorithm SHA256).Hash
    if ($hash -ne $manifest.WeightSha256) { throw 'Policy weights checksum failed.' }
    $hash
}
$signalRows = [Collections.Generic.List[object]]::new()
$signalPrefixes = [Collections.Generic.List[object]]::new()
$signalAudits = [Collections.Generic.List[object]]::new()
foreach ($signalCase in @('identity','channel','action','noise8','noise32','neutral')) {
    foreach ($signalAge in @('fresh','late')) {
        $id = 'signal-' + $signalCase + '-' + $signalAge + '-01'
        $entry = Get-Content (Join-Path $signalMemory ('studies/' + $id + '.json')) -Raw | ConvertFrom-Json -AsHashtable
        $root = Join-Path $RepoRoot $entry.output
        $suite = Get-Content (Join-Path $root 'suite.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($suite.Status -ne 'completed') { throw ('Incomplete study: ' + $id) }
        $runs = @(Get-ChildItem $root -Filter result.json -Recurse | ForEach-Object {
            @{Result=(Get-Content $_.FullName -Raw | ConvertFrom-Json -AsHashtable);Directory=$_.DirectoryName}
        })
        if ($runs.Count -ne $suite.ExpectedRuns -or @($runs|Where-Object {$_.Result.Status -ne 'completed'}).Count -gt 0) { throw ('Failed/incomplete runs: ' + $id) }
        $auditFile = Join-Path $RepoRoot ('artifacts/research/' + $Campaign + '/audits/' + $id + '/report.json')
        $audit = Get-Content $auditFile -Raw | ConvertFrom-Json -AsHashtable
        if (!$audit.Passed) { throw ('Scenario audit failed: ' + $id) }
        $signalAudits.Add([ordered]@{Study=$id;Sha256=(Get-FileHash $auditFile -Algorithm SHA256).Hash;Report=$audit})
        foreach ($item in $runs) {
            $run = $item.Result
            $start = if ($signalAge -eq 'late') {20000} else {0}
            $windows = @($run.LifeMetrics | Where-Object Phase -eq 'changed' | Sort-Object Step)
            $consecutive = 0; $stable = $null
            foreach ($window in $windows) {
                if ($window.Metrics.'signal.success'.Value -ge .85) { $consecutive++ } else { $consecutive = 0 }
                if ($consecutive -eq 2 -and $null -eq $stable) { $stable = $window.Step - $start }
            }
            $curve = @(foreach ($at in @($start,($start+5000),($start+10000),($start+15000),($start+20000))) {
                $value = Success $run 'changed' $at
                if ($null -ne $value) { [ordered]@{Step=($at-$start);Success=$value} }
            })
            $signalRows.Add([ordered]@{
                Study=$id;Case=$signalCase;Age=$signalAge;Variant=$run.Variant;Seed=$run.Seed
                Final=(Success $run 'changed' $run.TrainingSteps)
                BaseFinal=$(if ($signalAge -eq 'late') {Success $run 'base' 20000} else {$null})
                BaseRetained=$(if ($signalAge -eq 'late') {Success $run 'base' 40000} else {$null})
                LiveAuc=(Mean @($windows | ForEach-Object {$_.Metrics.'signal.success'.Value}))
                LiveFirst=(Mean @($windows | Select-Object -First 2 | ForEach-Object {$_.Metrics.'signal.success'.Value}))
                LiveLast=(Mean @($windows | Select-Object -Last 2 | ForEach-Object {$_.Metrics.'signal.success'.Value}))
                StepsToTwoAdequateWindows=$stable;GreedyCurve=$curve
                LiveWindows=@($windows | ForEach-Object {[ordered]@{Step=($_.Step-$start);Success=$_.Metrics.'signal.success'.Value}})
                TrainingSteps=$run.TrainingSteps;Updates=$run.Learning[-1].Controller.Updates
                TrainingSeconds=$run.TrainingSeconds;EvaluationSeconds=$run.EvaluationSeconds
                ResultSha256=(Get-FileHash (Join-Path $item.Directory 'result.json') -Algorithm SHA256).Hash
                FinalWeightsSha256=(WeightHash $item.Directory 'changed-final')
            })
        }
        if ($signalAge -eq 'late') {
            foreach ($item in $runs | Where-Object {$_.Result.Variant.EndsWith('--learning')}) {
                $shadow = $runs | Where-Object {$_.Result.Seed -eq $item.Result.Seed -and $_.Result.Variant.EndsWith('--frozen')} | Select-Object -First 1
                $a = $item.Result.Learning | Where-Object {$_.Phase -eq 'base' -and $_.Step -eq 20000} | Select-Object -First 1
                $b = $shadow.Result.Learning | Where-Object {$_.Phase -eq 'base' -and $_.Step -eq 20000} | Select-Object -First 1
                $signalPrefixes.Add([ordered]@{Case=$signalCase;Seed=$item.Result.Seed;WorldEqual=($a.WorldHash -eq $b.WorldHash)
                    ActionsEqual=($a.ActionPrefixHash -eq $b.ActionPrefixHash);WeightsEqual=((WeightHash $item.Directory 'base-final') -eq (WeightHash $shadow.Directory 'base-final'))})
            }
        }
    }
}
$freeze = Get-Content (Join-Path $signalMemory 'generic-ac-freeze-01.json') -Raw | ConvertFrom-Json -AsHashtable
$integrity = @(foreach ($path in $freeze.sourceHashes.Keys) {
    [ordered]@{Path=$path;Unchanged=((Get-FileHash (Join-Path $RepoRoot $path) -Algorithm SHA256).Hash -eq $freeze.sourceHashes[$path])}
})
if (@($integrity | Where-Object {!$_.Unchanged}).Count -gt 0 -or
    @($signalPrefixes | Where-Object {!$_.WorldEqual -or !$_.ActionsEqual -or !$_.WeightsEqual}).Count -gt 0) { throw 'Freeze/prefix integrity failed.' }
$summary = [ordered]@{
    SchemaVersion=1;Role='open-capability-diagnostic';Runs=$signalRows.Count
    TrainingSteps=($signalRows.TrainingSteps | Measure-Object -Sum).Sum;ParameterUpdates=($signalRows.Updates | Measure-Object -Sum).Sum
    TrainingSeconds=($signalRows.TrainingSeconds | Measure-Object -Sum).Sum;EvaluationSeconds=($signalRows.EvaluationSeconds | Measure-Object -Sum).Sum
    Rows=$signalRows;MatchedPrefixes=$signalPrefixes;ControllerFreezeIntegrity=$integrity;Audits=$signalAudits
    Limitations=@('N=3 training seeds; evaluation worlds are nested repetitions, not additional learners',
        'Open IID immediate-feedback microdiagnostics, not held-out ecological generalization or algorithm ranking',
        'Neutral pre-exposure changes age/RNG/updates; it does not isolate a learned effect representation',
        'Greedy evaluation and actual-life exploratory behavior are distinct; report both',
        'A deficit shared by fresh/experienced agents is not evidence of loss of plasticity')
}
$destination = Join-Path $signalMemory ('results/' + $Output)
if (Test-Path $destination) { throw 'Preserve summaries; choose another output name.' }
$summary | ConvertTo-Json -Depth 35 | Set-Content $destination -Encoding utf8
$signalRows | ForEach-Object {[pscustomobject]$_} | Format-Table Case,Age,Variant,Seed,Final,LiveAuc,LiveLast,StepsToTwoAdequateWindows,BaseRetained
Write-Output ('Summary: ' + $destination)

#requires -Version 7.2
[CmdletBinding()]
param([string]$Campaign='local', [string]$Study='expanding-life-pilot-01')
. (Join-Path $PSScriptRoot 'common.ps1')
$summaryMemory = Join-Path $RepoRoot ('research/campaigns/' + $Campaign)
$summaryRegistration = Get-Content -LiteralPath (Join-Path $summaryMemory ('studies/' + $Study + '.json')) -Raw | ConvertFrom-Json -AsHashtable
$summaryRoot = Join-Path $RepoRoot $summaryRegistration.output
$summarySuite = Get-Content -LiteralPath (Join-Path $summaryRoot 'suite.json') -Raw | ConvertFrom-Json -AsHashtable
if ($summarySuite.Status -ne 'completed') { throw 'Only summarize a completed series; preserve partial trials.' }
$summaryRuns = @(Get-ChildItem -LiteralPath $summaryRoot -Filter result.json -Recurse | ForEach-Object {
    @{Result=(Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -AsHashtable);Directory=$_.DirectoryName}
})
if ($summaryRuns.Count -ne $summarySuite.ExpectedRuns -or @($summaryRuns | Where-Object {$_.Result.Status -ne 'completed'}).Count -gt 0) { throw 'Incomplete or failed trials.' }
$summaryAudit = Get-Content -LiteralPath (Join-Path $RepoRoot 'research/scenarios/expanding-life-v1-audit.json') -Raw | ConvertFrom-Json -AsHashtable
function Average($Values) {
    $present=@($Values)
    if ($present.Count -eq 0 -or @($present|Where-Object {$null -eq $_}).Count -gt 0) { return $null }
    return [double](($present|Measure-Object -Average).Average)
}
function Metric($Run, [string]$Scenario, [long]$At, [string]$Key, [string]$Kind='policy') {
    $episodes=@($Run.Evaluations | Where-Object {$_.Kind -eq $Kind -and $_.Scenario -eq $Scenario -and $_.AtStep -eq $At})
    if ($episodes.Count -eq 0 -or @($episodes|Where-Object {!$_.Metrics.ContainsKey($Key) -or $null -eq $_.Metrics[$Key].Value}).Count -gt 0) { return $null }
    return Average @($episodes | ForEach-Object {$_.Metrics[$Key].Value})
}
function WeightsHash([string]$Directory, [string]$Policy) {
    $policyRoot=Join-Path $Directory ('policies/' + $Policy)
    $pointer=Get-Content -LiteralPath (Join-Path $policyRoot 'current') -Raw | ConvertFrom-Json -AsHashtable
    $generation=Join-Path $policyRoot $pointer.Generation
    $manifest=Get-Content -LiteralPath (Join-Path $generation 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
    $actual=(Get-FileHash -LiteralPath (Join-Path $generation 'weights.bin') -Algorithm SHA256).Hash
    if ($actual -ne $manifest.WeightSha256) { throw 'Policy weights checksum failed.' }
    return $actual
}
$summaryActive=Average @($summaryAudit.Controls | Where-Object {$_.Condition -eq 2 -and $_.Policy -eq 'joint'} | ForEach-Object {$_.Metrics.'interaction.pressure-error'.Value})
$summaryPassive=@($summaryAudit.Controls | Where-Object {$_.Condition -eq 2 -and $_.Policy -in @('stay','always-plus','always-minus','random','thermal-only')} | Group-Object Policy | ForEach-Object {
    [pscustomobject]@{Policy=$_.Name;Error=(Average @($_.Group|ForEach-Object {$_.Metrics.'interaction.pressure-error'.Value}))}
}) | Sort-Object Error | Select-Object -First 1
$summaryGap=$summaryPassive.Error-$summaryActive
if ($summaryGap -le 0) { throw 'Scenario has no active/passive gap.' }
$summaryRows=@(foreach ($item in $summaryRuns) {
    $run=$item.Result
    $pressure=Metric $run 'need' 300000 'interaction.pressure-error'
    $entry=Metric $run 'need' 200000 'interaction.pressure-error' 'frozen'
    $shadow=$summaryRuns|Where-Object {$_.Result.Seed -eq $run.Seed -and $_.Result.Variant.EndsWith('--coverage-frozen')}|Select-Object -First 1
    $shadowError=Metric $shadow.Result 'need' 300000 'interaction.pressure-error'
    $windows=@($run.LifeMetrics|Where-Object {$_.Phase -eq 'need'})
    $consecutive=0; $firstStable=$null
    foreach ($window in $windows) {
        $q=($summaryPassive.Error-$window.Metrics.'interaction.pressure-error'.Value)/$summaryGap
        if ($q -ge 0.5) { $consecutive++ } else { $consecutive=0 }
        if ($consecutive -eq 2 -and $null -eq $firstStable) { $firstStable=$window.Step-200000 }
    }
    $needPolicy=Join-Path $item.Directory 'policies/need-final'
    $pointer=Get-Content (Join-Path $needPolicy 'current') -Raw|ConvertFrom-Json -AsHashtable
    $manifest=Get-Content (Join-Path (Join-Path $needPolicy $pointer.Generation) 'manifest.json') -Raw|ConvertFrom-Json -AsHashtable
    $null=WeightsHash $item.Directory 'need-final'
    [ordered]@{
        Variant=$run.Variant;Seed=$run.Seed;Pressure=$pressure;PressureAtNeedEntry=$entry
        Q=($summaryPassive.Error-$pressure)/$summaryGap
        GainVsOwnEntry=($entry-$pressure)/$summaryGap
        GainVsCoverageShadow=($shadowError-$pressure)/$summaryGap
        SpecificGate=($run.Variant.EndsWith('--coverage') -and ($summaryPassive.Error-$pressure)/$summaryGap -ge 0.5 -and ($shadowError-$pressure)/$summaryGap -ge 0.2)
        StepsToTwoAdequateWindows=$firstStable
        ThermalInitial=(Metric $run 'initial' 100000 'temperature.mae')
        ThermalRetained=(Metric $run 'initial' 300000 'temperature.mae')
        ThermalJoint=(Metric $run 'need' 300000 'temperature.mae')
        NeutralCommandFraction=(Average @($run.LifeMetrics|Where-Object Phase -eq 'interaction'|ForEach-Object {$_.Metrics.'interaction.command-fraction'.Value}))
        TrainingSeconds=$run.TrainingSeconds;EvaluationSeconds=$run.EvaluationSeconds
        Updates=$run.Learning[-1].Controller.Updates
        ExploratoryChoices=$manifest.ControllerState.exploratoryChoices
        CoverageEntries=$manifest.ControllerState.coverageEntries
    }
})
$summaryPrefixes=@(foreach ($item in $summaryRuns|Where-Object {$_.Result.Variant.EndsWith('--coverage')}) {
    $shadow=$summaryRuns|Where-Object {$_.Result.Seed -eq $item.Result.Seed -and $_.Result.Variant.EndsWith('--coverage-frozen')}|Select-Object -First 1
    $a=$item.Result.Learning|Where-Object {$_.Phase -eq 'initial' -and $_.Step -eq 100000}|Select-Object -First 1
    $b=$shadow.Result.Learning|Where-Object {$_.Phase -eq 'initial' -and $_.Step -eq 100000}|Select-Object -First 1
    [ordered]@{Seed=$item.Result.Seed;WorldEqual=($a.WorldHash -eq $b.WorldHash);ActionsEqual=($a.ActionPrefixHash -eq $b.ActionPrefixHash)
        WeightsEqual=((WeightsHash $item.Directory 'initial-final') -eq (WeightsHash $shadow.Directory 'initial-final'))}
})
$summaryFreeze=Get-Content (Join-Path $summaryMemory 'generic-ac-freeze-01.json') -Raw|ConvertFrom-Json -AsHashtable
$summaryIntegrity=@(foreach ($path in $summaryFreeze.sourceHashes.Keys) {
    [ordered]@{Path=$path;Unchanged=((Get-FileHash -LiteralPath (Join-Path $RepoRoot $path) -Algorithm SHA256).Hash -eq $summaryFreeze.sourceHashes[$path])}
})
$summaryResult=[ordered]@{
    SchemaVersion=1;Study=$Study;Runs=$summaryRuns.Count;Transitions=($summaryRuns.Result.TrainingSteps|Measure-Object -Sum).Sum
    AuditActive=$summaryActive;AuditBestPassive=$summaryPassive;AuditGap=$summaryGap
    CoverageAcquisitionSeeds=@($summaryRows|Where-Object SpecificGate -eq $true).Count
    CoverageAcquisitionConfirmedInPilot=(@($summaryRows|Where-Object SpecificGate -eq $true).Count -ge 2)
    FullAdequacy='not-established: initial thermal skill was not reliably acquired; retention cannot be certified'
    Rows=$summaryRows;MatchedPrefixes=$summaryPrefixes;ControllerFreezeIntegrity=$summaryIntegrity
    Limitations=@('N=3 per arm; one new remote interaction and one order','Exploration coverage is not prediction knowledge','No causal isolation of neutral pre-exposure transfer','No algorithm-family ranking or universal adaptation claim')
}
if (@($summaryPrefixes|Where-Object {!$_.WorldEqual -or !$_.ActionsEqual -or !$_.WeightsEqual}).Count -gt 0 -or
    @($summaryIntegrity|Where-Object {!$_.Unchanged}).Count -gt 0) { throw 'Frozen-prefix or controller-freeze check failed.' }
$summaryDestination=Join-Path $summaryMemory ('results/' + $Study + '-summary.json')
if (Test-Path -LiteralPath $summaryDestination) { throw 'Summary already exists; preserve it.' }
$summaryResult|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $summaryDestination -Encoding utf8
$summaryRows|ForEach-Object {[pscustomobject]$_}|Format-Table Variant,Seed,Pressure,Q,GainVsCoverageShadow,ThermalJoint
Write-Output ('Summary: ' + $summaryDestination)

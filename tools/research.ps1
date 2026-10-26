#requires -Version 7.2
[CmdletBinding()]
param(
    [ValidateSet('hardware','init','session','add','run','stop','context','note','backup')][string]$Action = 'context',
    [string]$Campaign = 'local', [string]$Id, [string]$Branch, [string]$Config, [string]$Hypothesis,
    [ValidateSet('pilot','full','diagnostic')][string]$Stage = 'pilot', [string]$DecisionRule,
    [string[]]$Metrics = @('temperature.mae','hunger.mean','wildlife.proximity'),
    [string]$Text, [string]$Next, [string]$ScenarioReview, [double]$Minutes = 0, [int]$Parallel = 0, [int]$Threads = 0,
    [double]$TrialTimeoutMinutes = 0, [switch]$Force, [switch]$Resume, [string]$Destination
)
. (Join-Path $PSScriptRoot 'common.ps1')
if ($Campaign -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]*$') { throw 'Use a simple campaign id.' }
$memoryRoot = Join-Path $RepoRoot ('research/campaigns/' + $Campaign)
$artifactRoot = Join-Path $RepoRoot ('artifacts/research/' + $Campaign)
$campaignPath = Join-Path $memoryRoot 'campaign.json'
$stopPath = Join-Path $artifactRoot 'STOP'

function Read-Json([string]$Path) { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable }
function Write-AtomicJson([string]$Path, $Value) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path + '.tmp', ($Value | ConvertTo-Json -Depth 60), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($Path + '.tmp', $Path, $true)
}
function Get-Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function Git-Value([string[]]$Arguments) {
    $value = & git -C $RepoRoot @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git failed: $Arguments" }
    ($value -join "`n").Trim()
}
function Assert-Branch($Settings) {
    if ((Git-Value @('branch','--show-current')) -ne $Settings.branch) { throw "Switch to campaign branch $($Settings.branch) before changing or running this campaign." }
}
function Add-Journal([string]$Kind, $Data) {
    $line = @{utc=[DateTimeOffset]::UtcNow.ToString('o');kind=$Kind;data=$Data} | ConvertTo-Json -Compress -Depth 30
    [IO.File]::AppendAllText((Join-Path $memoryRoot 'journal.jsonl'), $line + "`n", [Text.UTF8Encoding]::new($false))
}
function Get-Hardware {
    $cpu = @(Get-CimInstance Win32_Processor)
    $os = Get-CimInstance Win32_OperatingSystem
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($artifactRoot))
    $gpuDetails=@()
    if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { $gpuDetails=@(& nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader 2>$null) }
    [ordered]@{
        capturedUtc=[DateTimeOffset]::UtcNow.ToString('o');cpu=($cpu.Name -join ', ')
        physicalCores=[int](($cpu | Measure-Object NumberOfCores -Sum).Sum)
        logicalProcessors=[Environment]::ProcessorCount;memoryGiB=[math]::Round($os.TotalVisibleMemorySize/1MB,2)
        availableMemoryGiB=[math]::Round($os.FreePhysicalMemory/1MB,2)
        freeDiskGiB=[math]::Round($drive.AvailableFreeSpace/1GB,2)
        gpu=@(Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name)
        gpuDetails=$gpuDetails
        powerShell=$PSVersionTable.PSVersion.ToString();dqnBackend='CPU; GPU requires a separately implemented/validated backend'
    }
}
function Get-Entries {
    @(Get-ChildItem -LiteralPath (Join-Path $memoryRoot 'studies') -Filter '*.json' -ErrorAction SilentlyContinue |
        Sort-Object Name | ForEach-Object { Read-Json $_.FullName })
}
function Get-StudyStatus($Entry) {
    $suite = Join-Path $RepoRoot ($Entry.output + '/suite.json')
    if (Test-Path -LiteralPath $suite) { (Read-Json $suite).Status } else { 'registered' }
}
function Write-Context {
    $settings = Read-Json $campaignPath
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("# Campaign $Campaign")
    $lines.Add("`nBranch: $($settings.branch). Current: $(Git-Value @('branch','--show-current')).")
    $lines.Add("`nRead research/RESEARCHER.md, research/BASELINE.md, campaign.json, FINDINGS.md (if present) and the most recent journal entries before acting.")
    $lines.Add("`nCompute defaults: $($settings.maxParallel) workers, $($settings.threadsPerWorker) threads each; slice $($settings.computeSliceMinutes) min; reserve $($settings.reserveFreeMemoryGiB) GiB free RAM.")
    if (Test-Path -LiteralPath (Join-Path $artifactRoot 'session.json')) {
        $session = Read-Json (Join-Path $artifactRoot 'session.json')
        $deadlineUtc = ([DateTimeOffset]$session.deadlineUtc).ToUniversalTime().ToString('o')
        $lines.Add("`nLast session deadline (UTC): $deadlineUtc. An expired deadline needs a new explicit session budget.")
    }
    $lines.Add("`nStop requested: $(Test-Path -LiteralPath $stopPath).")
    $baselinePath=Join-Path $RepoRoot 'research/baseline.json'
    if (Test-Path -LiteralPath $baselinePath) {
        $baseline=Read-Json $baselinePath
        $lines.Add("`nCurrent comparison baseline: $($baseline.id), $($baseline.experiment). Scope: $($baseline.scope) Review: $($baseline.review). Additional factors require their own passing review; repair trivial scenarios before scientific pilot/full runs.")
    }
    $lines.Add("`n| Study | Stage | Status | Hypothesis | Full artifacts |`n|---|---|---|---|---|")
    foreach ($entry in Get-Entries) {
        $lines.Add("| $($entry.id) | $($entry.stage) | $(Get-StudyStatus $entry) | $($entry.hypothesis.statement.Replace('|','/')) | $($entry.output) |")
        if ($entry.stage -eq 'full') { $lines.Add("`nFull-study decision rule: $($entry.decisionRule)") }
    }
    $journal = Join-Path $memoryRoot 'journal.jsonl'
    if (Test-Path -LiteralPath $journal) {
        $lines.Add("`n## Recent decisions/events`n")
        foreach ($line in @(Get-Content -LiteralPath $journal -Tail 12)) { $lines.Add($line) }
    }
    $lines.Add("`n## Resume in Codex`n")
    $lines.Add("Continue campaign '$Campaign' on branch '$($settings.branch)'. Read this file, research/RESEARCHER.md, research/BASELINE.md, the immutable study registrations and referenced evidence. Reconcile suite.json/result.json with journal before launching anything. Preserve completed runs; do not treat reward as success. Check the session deadline. Record the next decision and next action before ending the session.")
    [IO.File]::WriteAllText((Join-Path $memoryRoot 'CONTEXT.md'), ($lines -join "`n"), [Text.UTF8Encoding]::new($false))
    $lines -join "`n"
}
function Invoke-Executor([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    $script:ResearchExitCode = $LASTEXITCODE
}
function Get-Bundle {
    $source = Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0'
    if (!(Test-Path -LiteralPath (Join-Path $source 'ArtificialLife.Cli.exe'))) { throw 'Build the headless Release CLI first.' }
    $files = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse | Sort-Object FullName) {
        if ($file.Extension -in @('.dll','.exe','.json')) { $files[[IO.Path]::GetRelativePath($source,$file.FullName)] = Get-Hash $file.FullName }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($files | ConvertTo-Json -Compress))
    $identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    $bundle = Join-Path $artifactRoot ('bundles/' + $identity)
    if (!(Test-Path -LiteralPath (Join-Path $bundle 'bundle.json'))) {
        [IO.Directory]::CreateDirectory($bundle) | Out-Null
        foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
            $target = Join-Path $bundle ([IO.Path]::GetRelativePath($source,$file.FullName))
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
        Write-AtomicJson (Join-Path $bundle 'bundle.json') @{schemaVersion=1;identity=$identity;files=$files}
    }
    foreach ($relative in $files.Keys) {
        if ((Get-Hash (Join-Path $bundle $relative)) -ne $files[$relative]) { throw "Bundle checksum mismatch: $relative" }
    }
    $bundle
}

if ($Action -eq 'hardware') { Get-Hardware | ConvertTo-Json -Depth 5; return }
if ($Action -eq 'init') {
    if (Test-Path -LiteralPath $campaignPath) { Write-Context; return }
    if (!$Branch) { $Branch = 'master' }
    $current = Git-Value @('branch','--show-current')
    if ($current -ne $Branch) {
        $existing = & git -C $RepoRoot branch --list $Branch
        if ($existing) { throw "Branch $Branch exists; switch to it first." }
        & git -C $RepoRoot switch -c $Branch
        if ($LASTEXITCODE -ne 0) { throw 'Cannot create research branch.' }
    }
    [IO.Directory]::CreateDirectory((Join-Path $memoryRoot 'studies')) | Out-Null
    [IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
    $settings = [ordered]@{schemaVersion=1;id=$Campaign;branch=$Branch;createdUtc=[DateTimeOffset]::UtcNow.ToString('o');maxParallel=2;threadsPerWorker=1;reserveFreeMemoryGiB=2;workerMemoryEstimateGiB=2;minFreeDiskGiB=10;computeSliceMinutes=10;sessionMinutes=30;trialTimeoutMinutes=120}
    Write-AtomicJson $campaignPath $settings
    Write-AtomicJson (Join-Path $memoryRoot 'hardware.json') (Get-Hardware)
    Add-Journal 'initialized' @{branch=$Branch;next='Read research/BASELINE.md and choose a question; no full campaign has started.'}
    Write-Context; return
}
if (!(Test-Path -LiteralPath $campaignPath)) { throw "Initialize campaign '$Campaign' first." }
$settings = Read-Json $campaignPath
if ($Action -eq 'context') { Write-Context; return }
if ($Action -eq 'stop') {
    [IO.Directory]::CreateDirectory($artifactRoot) | Out-Null
    [IO.File]::WriteAllText($stopPath + '.tmp', $(if ($Force) {'force'} else {'drain'}))
    [IO.File]::Move($stopPath + '.tmp',$stopPath,$true)
    if ($Force) {
        $orphanLease=$null
        try { $orphanLease=[IO.File]::Open((Join-Path $artifactRoot 'coordinator.lock'),'OpenOrCreate','ReadWrite','None') } catch [IO.IOException] { }
        if ($null -ne $orphanLease) {
            try {
                foreach ($entry in Get-Entries) {
                    $resultRoot=Join-Path $RepoRoot $entry.output
                    if (!(Test-Path -LiteralPath $resultRoot)) { continue }
                    $expectedExecutable=Join-Path (Join-Path $RepoRoot $entry.bundle) 'ArtificialLife.Cli.exe'
                    foreach ($file in Get-ChildItem -LiteralPath $resultRoot -Filter process.json -Recurse) {
                        $record=Read-Json $file.FullName
                        $worker=Get-Process -Id $record.Id -ErrorAction SilentlyContinue
                        if ($null -eq $worker) { continue }
                        # PID reuse must never terminate an unrelated process.
                        try {
                            if ($worker.StartTime.ToUniversalTime() -eq ([DateTimeOffset]$record.StartedUtc).UtcDateTime -and $worker.Path -eq $expectedExecutable) {
                                $worker.Kill($true); $worker.WaitForExit()
                            }
                        } catch [InvalidOperationException] { } finally { $worker.Dispose() }
                    }
                }
            } finally { $orphanLease.Dispose() }
        }
    }
    Add-Journal 'stop-requested' @{force=[bool]$Force;next='Resume explicitly; incomplete trials restart from their seed.'}
    return
}
Assert-Branch $settings
if ($Action -eq 'session') {
    if ($Minutes -le 0) { $Minutes = $settings.sessionMinutes }
    Write-AtomicJson (Join-Path $artifactRoot 'session.json') @{startedUtc=[DateTimeOffset]::UtcNow.ToString('o');deadlineUtc=[DateTimeOffset]::UtcNow.AddMinutes($Minutes).ToString('o')}
    Add-Journal 'session-started' @{minutes=$Minutes}
    Write-Context; return
}
if ($Action -eq 'note') {
    if (!$Text -or !$Next) { throw 'Notes require -Text (analysis/decision, including negative findings) and -Next.' }
    Add-Journal 'decision' @{study=$Id;text=$Text;next=$Next}
    $subject=if ($Id) { $Id } else { $Campaign }
    [IO.File]::AppendAllText((Join-Path $memoryRoot 'FINDINGS.md'), "`n## $([DateTimeOffset]::UtcNow.ToString('o')) — $subject`n`n$Text`n`nNext: $Next`n", [Text.UTF8Encoding]::new($false))
    Write-Context; return
}
if ($Action -eq 'add') {
    $baselinePath=Join-Path $RepoRoot 'research/baseline.json'
    $baseline=if (Test-Path -LiteralPath $baselinePath) { Read-Json $baselinePath } else { $null }
    if (!$Config -and $null -ne $baseline) { $Config=Join-Path $RepoRoot $baseline.experiment }
    if (!$ScenarioReview -and $Stage -ne 'diagnostic' -and $null -ne $baseline) { $ScenarioReview=Join-Path $RepoRoot $baseline.review }
    if ($Id -notmatch '^[a-zA-Z0-9][a-zA-Z0-9_-]*$' -or !$Config -or !$Hypothesis -or !$DecisionRule -or $Metrics.Count -eq 0) { throw 'Add requires -Id, -Config (or the registered baseline), -Hypothesis, -DecisionRule and behavioral -Metrics.' }
    if ($Stage -ne 'diagnostic' -and !$ScenarioReview) { throw 'Scientific pilot/full studies require a passing -ScenarioReview for every active factor. Repair trivial scenarios first; use diagnostic for the repair checks.' }
    $registration = Join-Path $memoryRoot ('studies/' + $Id + '.json')
    $studyRoot = Join-Path $artifactRoot ('studies/' + $Id)
    if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $studyRoot)) { throw 'Study ids are immutable. Use a new id (including for a repaired preflight).' }
    $idea = Read-Json ([IO.Path]::GetFullPath($Hypothesis))
    if (!$idea.statement -or !$idea.direction -or !$idea.rationale -or !$idea.falsification -or !$idea.ContainsKey('sources')) { throw 'Hypothesis needs statement, direction, rationale, falsification and sources (possibly empty for an original hypothesis).' }
    $hardware = Get-Hardware
    if ($hardware.freeDiskGiB -lt $settings.minFreeDiskGiB) { throw 'Insufficient free disk for retained artifacts.' }
    Initialize-Dotnet
    $bundle = Get-Bundle
    $executor = Join-Path $bundle 'ArtificialLife.Cli.exe'
    if ($Stage -ne 'diagnostic') {
        Invoke-Executor $executor @('scenario-validate-review','--config',[IO.Path]::GetFullPath($Config),'--review',[IO.Path]::GetFullPath($ScenarioReview))
        if ($script:ResearchExitCode -ne 0) { throw 'Scenario suitability rejected: repair/re-audit the scenario instead of merely documenting an ineffective factor.' }
    }
    Invoke-Executor $executor @('experiment-prepare','--config',[IO.Path]::GetFullPath($Config),'--output',$studyRoot)
    if ($script:ResearchExitCode -ne 0) { throw 'Experiment preflight failed. Partial preparation is retained; repair using a new study id.' }
    $plan = Read-Json (Join-Path $studyRoot 'plan.json')
    $spec = Read-Json (Join-Path $studyRoot 'config.json')
    $reviewHash=$null
    if ($ScenarioReview) {
        $reviewPath=[IO.Path]::GetFullPath($ScenarioReview)
        $review=Read-Json $reviewPath
        $evidencePath=[IO.Path]::GetFullPath($review.Evidence,[IO.Path]::GetDirectoryName($reviewPath))
        Copy-Item -LiteralPath $evidencePath -Destination (Join-Path $studyRoot 'scenario-evidence.json')
        $review.Evidence='scenario-evidence.json'
        Write-AtomicJson (Join-Path $studyRoot 'scenario-review.json') $review
        $reviewHash=Get-Hash (Join-Path $studyRoot 'scenario-review.json')
        if ($Stage -ne 'diagnostic') {
            Invoke-Executor $executor @('scenario-validate-review','--config',(Join-Path $studyRoot 'config.json'),'--review',(Join-Path $studyRoot 'scenario-review.json'))
            if ($script:ResearchExitCode -ne 0) { throw 'Frozen scenario or copied evidence does not match the review.' }
        }
    }
    if ($Stage -eq 'full' -and $spec.TrainingSeeds.Count -lt 2) { throw 'A full multi-seed comparison needs multiple training seeds; single-seed exploration uses pilot/diagnostic.' }
    $entry = [ordered]@{
        schemaVersion=1;id=$Id;stage=$Stage;registeredUtc=[DateTimeOffset]::UtcNow.ToString('o');hypothesis=$idea
        decisionRule=$DecisionRule;metrics=$Metrics;plan=$plan;configSha256=(Get-Hash (Join-Path $studyRoot 'config.json'))
        bundle=[IO.Path]::GetRelativePath($RepoRoot,$bundle);output=[IO.Path]::GetRelativePath($RepoRoot,(Join-Path $studyRoot 'results'))
        sourceRevision=(Git-Value @('rev-parse','HEAD'));sourceStatus=(Git-Value @('status','--short'))
        scenarioReviewSha256=$reviewHash;scenarioCleared=($Stage -ne 'diagnostic')
    }
    Write-AtomicJson $registration $entry
    Copy-Item -LiteralPath $registration -Destination (Join-Path $studyRoot 'registration.json')
    Add-Journal 'registered' @{study=$Id;stage=$Stage;hypothesis=$idea.statement;rule=$DecisionRule;next='Run a bounded compute slice; inspect baselines and per-seed behavior.'}
    Write-Context; return
}
if ($Action -eq 'backup') {
    if (!$Destination) { throw 'Provide a new backup directory on a local disk.' }
    $target = [IO.Path]::GetFullPath($Destination)
    if ((Test-Path -LiteralPath $target) -or $target.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($memoryRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Backup must be a new directory outside campaign source directories.' }
    $busy = @(Get-ChildItem -LiteralPath $artifactRoot -Filter '*.lock' -Recurse | Where-Object {
        try { $lease=[IO.File]::Open($_.FullName,'Open','ReadWrite','None');$lease.Dispose();$false } catch { $true }
    })
    if ($busy.Count -gt 0) { throw 'Stop and drain all workers before a consistent backup.' }
    [IO.Directory]::CreateDirectory($target) | Out-Null
    Copy-Item -LiteralPath $memoryRoot -Destination (Join-Path $target 'memory') -Recurse
    Copy-Item -LiteralPath $artifactRoot -Destination (Join-Path $target 'artifacts') -Recurse
    $checksums = @(Get-ChildItem -LiteralPath $target -File -Recurse | ForEach-Object { @{path=[IO.Path]::GetRelativePath($target,$_.FullName);sha256=(Get-Hash $_.FullName)} })
    Write-AtomicJson (Join-Path $target 'backup-manifest.json') @{createdUtc=[DateTimeOffset]::UtcNow.ToString('o');files=$checksums}
    return
}
if ($Action -eq 'run') {
    Initialize-Dotnet
    $lease = [IO.File]::Open((Join-Path $artifactRoot 'coordinator.lock'),'OpenOrCreate','ReadWrite','None')
    $campaignExit=0
    try {
        if (Test-Path -LiteralPath $stopPath) {
            if (!$Resume) { throw 'Campaign has a stop request. Use -Resume explicitly.' }
            Remove-Item -LiteralPath $stopPath
        }
        if ($Minutes -le 0) { $Minutes=$settings.computeSliceMinutes }
        $sessionPath = Join-Path $artifactRoot 'session.json'
        if (Test-Path -LiteralPath $sessionPath) {
            $session = Read-Json $sessionPath
            $remaining = (([DateTimeOffset]$session.deadlineUtc)-[DateTimeOffset]::UtcNow).TotalMinutes
            if ($remaining -le 0) { throw 'Session deadline expired. Start a new explicitly budgeted session.' }
            $Minutes=[math]::Min($Minutes,$remaining)
        }
        $hardware=Get-Hardware
        if ($Parallel -le 0) { $Parallel=$settings.maxParallel }
        if ($Threads -le 0) { $Threads=$settings.threadsPerWorker }
        if ($TrialTimeoutMinutes -le 0) { $TrialTimeoutMinutes=$settings.trialTimeoutMinutes }
        $memorySlots=[math]::Floor(($hardware.availableMemoryGiB-$settings.reserveFreeMemoryGiB)/$settings.workerMemoryEstimateGiB)
        $cpuSlots=[math]::Floor([math]::Max(1,$hardware.physicalCores-2)/$Threads)
        $workers=[int][math]::Min($Parallel,[math]::Min($memorySlots,$cpuSlots))
        if ($workers -lt 1 -or $hardware.freeDiskGiB -lt $settings.minFreeDiskGiB) { throw 'Not enough currently free memory/CPU/disk for one worker at the requested settings.' }
        Write-AtomicJson (Join-Path $artifactRoot 'last-execution.json') @{hardware=$hardware;parallel=$workers;threads=$Threads;minutes=$Minutes;timeoutMinutes=$TrialTimeoutMinutes}
        Add-Journal 'compute-started' @{parallel=$workers;threads=$Threads;minutes=$Minutes}
        $timer=[Diagnostics.Stopwatch]::StartNew()
        $entries=Get-Entries
        if ($Id -and !@($entries | Where-Object id -eq $Id).Count) { throw "Unknown study: $Id" }
        foreach ($entry in $entries) {
            if ($Id -and $entry.id -ne $Id) { continue }
            if ($timer.Elapsed.TotalMinutes -ge $Minutes -or (Test-Path -LiteralPath $stopPath)) { break }
            $studyRoot=Join-Path $artifactRoot ('studies/' + $entry.id)
            $frozenConfig=Join-Path $studyRoot 'config.json'
            if ((Get-Hash $frozenConfig) -ne $entry.configSha256) { throw "Frozen config changed: $($entry.id). Register a new study." }
            $bundle=Join-Path $RepoRoot $entry.bundle
            $bundleManifest=Read-Json (Join-Path $bundle 'bundle.json')
            foreach ($relative in $bundleManifest.files.Keys) {
                if ((Get-Hash (Join-Path $bundle $relative)) -ne $bundleManifest.files[$relative]) { throw 'Frozen executable bundle changed.' }
            }
            if ((Get-StudyStatus $entry) -eq 'completed') { continue }
            if ($entry.stage -ne 'diagnostic' -and (!$entry.ContainsKey('scenarioCleared') -or !$entry.scenarioCleared)) {
                throw 'Unfinished historical study has no passing scenario review. Preserve it; repair/audit and register a new scientific study, or use diagnostic for investigation.'
            }
            $executor=Join-Path $bundle 'ArtificialLife.Cli.exe'
            if ($entry.ContainsKey('scenarioCleared') -and $entry.scenarioCleared) {
                $reviewPath=Join-Path $studyRoot 'scenario-review.json'
                if ((Get-Hash $reviewPath) -ne $entry.scenarioReviewSha256) { throw 'Registered scenario review changed.' }
                Invoke-Executor $executor @('scenario-validate-review','--config',$frozenConfig,'--review',$reviewPath)
                if ($script:ResearchExitCode -ne 0) { throw 'Scenario review/evidence must remain valid before scientific execution.' }
            }
            $left=($Minutes-$timer.Elapsed.TotalMinutes).ToString('R',[Globalization.CultureInfo]::InvariantCulture)
            $runnerArgs=@('experiment','--config',$frozenConfig,'--output',(Join-Path $RepoRoot $entry.output),'--parallel',"$workers",'--threads-per-worker',"$Threads",'--max-minutes',$left,'--trial-timeout-minutes',$TrialTimeoutMinutes.ToString('R',[Globalization.CultureInfo]::InvariantCulture),'--stop-file',$stopPath)
            Invoke-Executor $executor $runnerArgs
            $exit=$script:ResearchExitCode
            if ($exit -eq 1 -or ($exit -ne 0 -and $exit -ne 130)) { $campaignExit=1 }
            elseif ($exit -eq 130 -and $campaignExit -eq 0) { $campaignExit=130 }
            Add-Journal 'compute-returned' @{study=$entry.id;exit=$exit;status=(Get-StudyStatus $entry);next='Inspect per-seed comparisons; record interpretation and a next step. Completion is not behavioral success.'}
            $resultPath=Join-Path $RepoRoot ($entry.output + '/suite.json')
            if (Test-Path -LiteralPath $resultPath) {
                Invoke-Executor $executor @('compare','--inputs',(Join-Path $RepoRoot $entry.output),'--output',(Join-Path $studyRoot 'analysis'))
            }
            if ($exit -eq 130) { break }
        }
    } finally { $lease.Dispose(); Write-Context }
    exit $campaignExit
}

[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('check','deploy','observe')][string]$Action,
      [string]$Description='', [string]$PackageId='', [string]$Version='', [string[]]$Sources=@(),
      [string]$PackageRoot='artifacts/packages', [string]$Directory='', [string]$Session='',
      [string]$Checkpoint='', [string]$Output='', [string]$Id='', [string]$Cli='',
      [string[]]$TestClass=@(), [string]$TestProject='tests/ArtificialLife.Tests',
      [switch]$NoBuild, [switch]$NoTests, [switch]$Capture, [switch]$Save,
      [int]$TimeoutSeconds=120)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$cliPath=Get-LiveCli $Cli
if (!$Output) { $Output=Join-Path $RepoRoot ('artifacts/iterations/'+[Guid]::NewGuid().ToString('N')) }
$Output=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$summaryPath=Join-Path $Output 'result.json'
$request=[ordered]@{action=$Action;description=$Description;package=$PackageId;version=$Version;packageRoot=$PackageRoot;sources=$Sources;directory=$Directory;session=$Session;checkpoint=$Checkpoint;testClass=$TestClass;testProject=$TestProject;noTests=[bool]$NoTests;capture=[bool]$Capture;save=[bool]$Save}
$result=[ordered]@{status='running';id=$(if($Id){$Id}else{'evolve-'+[Guid]::NewGuid().ToString('N')});started=[DateTimeOffset]::UtcNow.ToString('o');request=$request;checks=@{};deployment='not-requested';errors=@();artifacts=@{result=$summaryPath}}
if (Test-Path -LiteralPath $summaryPath) {
    $previous=Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json -AsHashtable
    if (($previous.request|ConvertTo-Json -Compress) -ne ($request|ConvertTo-Json -Compress)) { throw 'Output belongs to a different request. Use a new -Output.' }
    $result=$previous
    if ($result.status -eq 'passed') { $result | ConvertTo-Json -Depth 15 -Compress; return }
    $result.status='running'; $result.errors=@()
    if ($result.Contains('errorStage')) { $result.Remove('errorStage') }
}
$script:evolutionStage='setup'
function Invoke-EvolutionStage([string]$Name, [scriptblock]$Body) {
    $script:evolutionStage=$Name
    $log=Join-Path $Output ($Name+'.log')
    $timer=[Diagnostics.Stopwatch]::StartNew()
    try { & $Body *> $log; $result.checks[$Name]=@{status='passed';seconds=[Math]::Round($timer.Elapsed.TotalSeconds,3);log=$log} }
    catch { $result.checks[$script:evolutionStage]=@{status='failed';log=$log}; $_ | Out-String | Add-Content -LiteralPath $log; throw }
    Write-LiveJson $summaryPath $result
}
try {
    if ($Action -eq 'check') {
        if ($result.artifacts.Contains('packages') -and $result.checks.package.status -eq 'passed') {
            $Sources=@($result.artifacts.packages)
            if ($Description -and !$TestClass.Count) {
                $definition=Get-Content -LiteralPath $Description -Raw | ConvertFrom-Json -AsHashtable
                if ($definition.ContainsKey('testClasses')) { $TestClass=@($definition.testClasses) }
            }
            $Description='' # Resume from the already published immutable package, not a regenerated PCK.
        }
        if ($Description) {
            $descriptionPath=[IO.Path]::GetFullPath($Description)
            $definition=Get-Content -LiteralPath $descriptionPath -Raw | ConvertFrom-Json -AsHashtable
            if (!$TestClass.Count -and $definition.ContainsKey('testClasses')) { $TestClass=@($definition.testClasses) }
            if (!$NoBuild) {
                Invoke-EvolutionStage 'build' { Invoke-Dotnet build (Join-Path (Split-Path $descriptionPath) $definition.project) -c Release --verbosity quiet }
            }
            Invoke-EvolutionStage 'package' {
                & (Join-Path $PSScriptRoot 'package-module.ps1') -Description $descriptionPath -Id $PackageId -Version $Version -OutputRoot $PackageRoot -NoBuild -Cli $cliPath |
                    Set-Content -LiteralPath (Join-Path $Output 'packages.json') -Encoding utf8
            }
            $Sources=@((Get-Content -LiteralPath (Join-Path $Output 'packages.json') -Raw | ConvertFrom-Json).packages)
        } else {
            if (!$Sources.Count) { throw 'Choose -Description or already published -Sources.' }
            Invoke-EvolutionStage 'package' { & $cliPath package verify --sources ($Sources -join ';'); if($LASTEXITCODE){throw 'Package verification failed.'} }
        }
        $result.artifacts.packages=$Sources
        Write-LiveJson $summaryPath $result
        if (!$NoTests -and !($result.checks.Contains('test') -and $result.checks.test.status -eq 'passed')) {
            if (!$TestClass.Count) { throw 'Specify relevant -TestClass filters (or descriptor testClasses), or explicitly use -NoTests.' }
            if (!$NoBuild) { Invoke-EvolutionStage 'test-build' { Invoke-Dotnet build $TestProject -c Release --no-restore --property:BuildProjectReferences=false --verbosity quiet } }
            Invoke-EvolutionStage 'test' {
                $testArgs=@('test','--project',$TestProject,'-c','Release','--no-build','--report-xunit-trx','--results-directory',(Join-Path $Output 'tests'))
                foreach ($filter in $TestClass) { $testArgs+=@('--filter-class',$filter) }
                Invoke-Dotnet @testArgs
                $reports=@(Get-ChildItem (Join-Path $Output 'tests') -Filter '*.trx' -Recurse)
                if (!$reports.Count) { throw 'Test runner produced no TRX report.' }
                foreach($report in $reports) {
                    [xml]$trx=Get-Content -LiteralPath $report.FullName -Raw
                    $counters=$trx.TestRun.ResultSummary.Counters
                    if ([int]$counters.executed -eq 0 -or [int]$counters.failed -gt 0 -or [int]$counters.notExecuted -gt 0) { throw 'Tests were empty, skipped or failed; see TRX.' }
                }
            }
            $result.artifacts.tests=Join-Path $Output 'tests'
        }
        if ($Checkpoint) {
            $validationOutput=Join-Path $Output 'validation'
            if (Test-Path -LiteralPath $validationOutput) { $validationOutput+='-'+[Guid]::NewGuid().ToString('N') }
            Invoke-EvolutionStage 'validation' { & (Join-Path $PSScriptRoot 'check-evolution.ps1') -Checkpoint $Checkpoint -Sources $Sources -Output $validationOutput -Cli $cliPath }
            $result.artifacts.validation=Join-Path $validationOutput 'validated.json'
        }
    } else {
        if (!$Directory) { throw 'Choose one World explicitly with -Directory; -Session can additionally pin its identity.' }
        $Directory=[IO.Path]::GetFullPath($Directory)
        $worldSession=(Get-Content -LiteralPath (Join-Path $Directory 'session-id') -Raw).Trim()
        if ($Session -and $Session -ne $worldSession) { throw 'Session does not belong to Directory.' }
        $Session=$worldSession
        $baselinePath=Join-Path $Output 'before.json'
        Invoke-EvolutionStage 'baseline' {
            if (!(Test-Path -LiteralPath $baselinePath)) { Write-LiveJson $baselinePath (Get-LiveSnapshot $cliPath $Session) }
        }
        $before=Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json -Depth 100
        if ($before.state.fault) { throw 'World is faulted before the iteration.' }
        if ($Action -eq 'deploy') {
            if (!$Sources.Count) { throw 'Deploy needs immutable published -Sources.' }
            if ($before.state.paused) { throw 'Resume the world explicitly before deploying: probation needs physical steps.' }
            Invoke-EvolutionStage 'package' {
                $verified=Invoke-LiveJson $cliPath @('package','verify','--sources',($Sources -join ';'))
                Write-LiveJson (Join-Path $Output 'packages.json') $verified
            }
            Invoke-EvolutionStage 'validation' {
                & (Join-Path $PSScriptRoot 'activate.ps1') -Directory $Directory -Sources $Sources -Id $result.id -Cli $cliPath
                $completion=Wait-LiveResult $Directory $result.id $TimeoutSeconds
                Write-LiveJson (Join-Path $Output 'activation.json') $completion
                if (!$completion.Accepted) {
                    $activationStarted=@(Get-Content -LiteralPath (Join-Path $Directory 'events.jsonl') |
                        ForEach-Object { $_ | ConvertFrom-Json } | Where-Object { $_.Kind -eq 'activation-start' -and $_.Detail.Id -eq $result.id })
                    if ($activationStarted.Count) { $script:evolutionStage='activation' }
                    throw $completion.Message
                }
            }
            Invoke-EvolutionStage 'activation' { Get-Content -LiteralPath (Join-Path $Output 'activation.json') -Raw }
            $result.deployment='probation'
            Invoke-EvolutionStage 'probation' {
                $watch=[Diagnostics.Stopwatch]::StartNew()
                do {
                    $recovery=Get-Content -LiteralPath (Join-Path $Directory 'recovery.json') -Raw | ConvertFrom-Json
                    if (!$recovery.Pending) { break }
                    if ($watch.Elapsed.TotalSeconds -gt $TimeoutSeconds) { throw 'Probation completion timeout; retry the same request/output to continue observing.' }
                    Start-Sleep -Milliseconds 200
                } while ($true)
                $active=Get-LiveSnapshot $cliPath $Session
                $verified=Get-Content -LiteralPath (Join-Path $Output 'packages.json') -Raw | ConvertFrom-Json
                foreach($package in $verified.packages) {
                    if (!@($active.state.packages | Where-Object { $_.id -eq $package.Reference.Id -and $_.version -eq $package.Reference.Version }).Count) { throw 'Candidate is not active after probation (possibly rolled back).' }
                }
            }
        }
        Invoke-EvolutionStage 'observation' {
            & (Join-Path $PSScriptRoot 'observe-evolution.ps1') -Label after -Directory $Directory -Output $Output -Cli $cliPath
            $after=Get-Content -LiteralPath (Join-Path $Output 'after.json') -Raw | ConvertFrom-Json -Depth 100
            if ($after.state.fault -or $after.state.frame.age -lt $before.state.frame.age) { throw 'World fault or clock regression.' }
            if (!$after.state.paused -and $after.state.frame.age -eq $before.state.frame.age) {
                Start-Sleep -Milliseconds 300
                $progress=Get-LiveSnapshot $cliPath $Session
                if($progress.state.frame.age -le $after.state.frame.age){throw 'World is not advancing.'}
                Write-LiveJson (Join-Path $Output 'after.json') $progress
            }
            $faults=@(Get-ChildItem -LiteralPath $Directory -Filter 'events.jsonl*' | ForEach-Object {
                Get-Content -LiteralPath $_.FullName | ForEach-Object { $_ | ConvertFrom-Json } |
                    Where-Object { [DateTimeOffset]$_.At -ge [DateTimeOffset]$result.started -and $_.Kind -in @('fault','watchdog','recovery','metric-error','learning-state-lost','interrupted-update-recovery') }
            })
            if ($faults.Count) { Write-LiveJson (Join-Path $Output 'faults.json') $faults; throw 'Protected event log contains new faults/recovery events.' }
        }
        $after=Get-Content -LiteralPath (Join-Path $Output 'after.json') -Raw | ConvertFrom-Json -Depth 100
        $result.world=@{session=$Session;age=$after.state.frame.age;revision=$after.state.frame.revision;ageDelta=($after.state.frame.age-$before.state.frame.age)}
        $delta=@{}
        foreach($metric in $after.state.metrics.PSObject.Properties) {
            if ($before.state.metrics.PSObject.Properties.Name -contains $metric.Name) { $delta[$metric.Name]=[double]$metric.Value-[double]$before.state.metrics.($metric.Name) }
        }
        $result.metricsDelta=$delta; $result.artifacts.observation=Join-Path $Output 'after.json'
        if ($Capture -or $Action -eq 'deploy') {
            Invoke-EvolutionStage 'capture' { Invoke-LiveJson $cliPath @('world','screenshot','--session',$Session,'--target',(Join-Path $Output 'frame.png')) | Out-Null }
            $result.artifacts.capture=Join-Path $Output 'frame.png'
        }
        if ($Save -or $Action -eq 'deploy') {
            Invoke-EvolutionStage 'checkpoint' {
                $copy=Join-Path $Output 'checkpoint'
                # WebSocket checkpoint completion is synchronous and works while the owner is paused.
                Invoke-LiveJson $cliPath @('world','checkpoint','--session',$Session,'--target',$copy,'--id',($result.id+'-save')) | Out-Null
                $pointer=Get-Content -LiteralPath (Join-Path $copy 'current') -Raw | ConvertFrom-Json
                if (!(Test-Path -LiteralPath (Join-Path $copy ($pointer.Generation+'/world.json')))) { throw 'Checkpoint was not confirmed.' }
                $result.artifacts.checkpoint=$copy
            }
        }
        if ($Action -eq 'deploy') { $result.deployment='committed' }
    }
    $result.status='passed'
} catch {
    $result.status='failed'; $result.errorStage=$script:evolutionStage
    $result.errors=@($_.Exception.Message)
}
Write-LiveJson $summaryPath $result
$result | ConvertTo-Json -Depth 15 -Compress
if ($result.status -eq 'failed') { exit 1 }

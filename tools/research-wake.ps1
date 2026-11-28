<#
.SYNOPSIS
Schedules one bounded continuation of the live World Only research.
.DESCRIPTION
Runs through Windows Task Scheduler under the signed-in user's existing Codex
login. Preview checks dispatch eligibility without calling Codex. Status shows
the Windows trigger and wake log. Disable cancels future attempts.
Logs and the at-most-once dispatch receipt are in artifacts/research/world-wake.
An accepted dispatch consumes the wake even when Codex fails, since it may
already have started research. Scheduler repetitions only retry preflight.
#>
[CmdletBinding()]
param(
    [ValidateSet('Register', 'Status', 'Disable', 'Run', 'Preview')]
    [string]$Action = 'Status',
    [Guid]$ThreadId,
    [DateTimeOffset]$At,
    [ValidateRange(5, 60)][int]$WorkMinutes = 30
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $repo 'artifacts/research/world-wake'
$statePath = Join-Path $stateDir 'schedule.json'
$receipt = Join-Path $stateDir 'dispatch.json'
$taskName = 'Live-WorldResearch-CodexWake-'
$sha = [Security.Cryptography.SHA256]::Create()
try {
    $taskName += [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($repo.ToLowerInvariant()))).Replace('-', '').Substring(0, 12)
} finally { $sha.Dispose() }

function Log([string]$Message) {
    [IO.File]::AppendAllText((Join-Path $stateDir 'wake.log'), ('{0:o} {1}{2}' -f [DateTimeOffset]::UtcNow, $Message, [Environment]::NewLine))
    Write-Output $Message
}

function Test-Held([string]$Path) {
    try {
        $file = [IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
        $file.Dispose()
        return $false
    } catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -is [IO.FileNotFoundException] -or $cause -is [IO.DirectoryNotFoundException]) { return $false }
        if ($cause -is [IO.IOException] -and (($cause.HResult -band 0xffff) -in 32, 33)) { return $true }
        throw
    }
}

function Get-ResearchRoute($Schedule) {
    $history = $Schedule.rollout
    if (!(Test-Path -LiteralPath $history -PathType Leaf)) {
        return 'defer: original chat history is unavailable'
    }
    $lastEvent = $null
    $lastStarted = $null
    $sharing = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $reader = [IO.StreamReader]::new([IO.File]::Open($history, 'Open', 'Read', $sharing))
    try {
        while (($line = $reader.ReadLine()) -ne $null) {
            if ($line -notmatch '"type"\s*:\s*"(task_started|task_complete|turn_aborted)"') { continue }
            $record = $line | ConvertFrom-Json
            if ($record.type -ne 'event_msg' -or $record.payload.type -notin 'task_started','task_complete','turn_aborted') { continue }
            $lastEvent = $record.payload
            if ($lastEvent.type -eq 'task_started') { $lastStarted = $lastEvent.turn_id }
        }
    } finally { $reader.Dispose() }
    if (!$lastStarted -or $lastEvent.type -eq 'task_started' -or $lastEvent.turn_id -ne $lastStarted) {
        return 'defer: original chat turn is active or its completion is unverified'
    }
    $campaign = Join-Path $repo 'artifacts/research/local'
    if (Test-Held (Join-Path $campaign 'coordinator.lock')) {
        return 'defer: a research coordinator is active'
    }
    foreach ($lock in Get-ChildItem -LiteralPath (Join-Path $campaign 'studies') -Filter worker.lock -File -Recurse -ErrorAction SilentlyContinue) {
        if (Test-Held $lock.FullName) { return 'defer: a research experiment worker is active' }
    }
    if (!(Test-Path -LiteralPath (Join-Path $repo 'research/WORLD.md') -PathType Leaf)) {
        return 'defer: the World Only handoff is unavailable'
    }
    $branch = & git -C $repo branch --show-current
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'master') { return 'defer: the project branch is not master' }
    if (Test-Held (Join-Path $Schedule.codexHome ('thread-writer-locks/' + $Schedule.threadId + '.lock'))) {
        # The desktop can retain writer ownership even after the matching task_complete.
        return 'fresh'
    }
    return 'resume'
}

function Get-CodexQuotaBlock($Schedule) {
    $process = [Diagnostics.Process]::new()
    try {
        $process.StartInfo.FileName = @($Schedule.codex)[0]
        $process.StartInfo.Arguments = 'app-server --stdio'
        $process.StartInfo.WorkingDirectory = $repo
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardInput = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        $process.StartInfo.EnvironmentVariables['CODEX_HOME'] = $Schedule.codexHome
        $started = $process.Start()
        if (!$started) { return 'quota service could not start' }
        $discard = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        $channel = $process.StandardInput
        $channel.WriteLine('{"id":1,"method":"initialize","params":{"clientInfo":{"name":"live-research-wake","version":"1"}}}')
        $channel.WriteLine('{"method":"initialized"}')
        $channel.WriteLine('{"id":2,"method":"account/rateLimits/read"}')
        $channel.Flush()
        $until = [DateTimeOffset]::UtcNow.AddSeconds(30)
        while ([DateTimeOffset]::UtcNow -lt $until) {
            $read = $process.StandardOutput.ReadLineAsync()
            if (!$read.Wait([int][Math]::Max(1, ($until - [DateTimeOffset]::UtcNow).TotalMilliseconds)) -or $null -eq $read.Result) { break }
            $response = $read.Result | ConvertFrom-Json
            if ($response.id -ne 2) { continue }
            if ($response.error -or !$response.result.rateLimits) { return 'Codex usage limits could not be verified' }
            $snapshot = $response.result.rateLimits
            $modelSnapshot = $response.result.rateLimitsByLimitId.PSObject.Properties[$Schedule.model]
            $snapshots = @($snapshot)
            if ($modelSnapshot) { $snapshots += $modelSnapshot.Value }
            foreach ($limit in $snapshots) {
                foreach ($window in @($limit.primary, $limit.secondary)) {
                    if ($window -and $window.usedPercent -ge 100) {
                        $reset = if ($window.resetsAt) { [DateTimeOffset]::FromUnixTimeSeconds($window.resetsAt).ToString('o') } else { 'unavailable' }
                        return "Codex usage exhausted, reported reset $reset"
                    }
                }
            }
            return
        }
        return 'Codex usage check timed out'
    } finally {
        if ($channel) { $channel.Close() }
        if ($started -and !$process.WaitForExit(1000)) { $process.Kill() }
        $process.Dispose()
    }
}

function Invoke-Codex($Schedule, [string]$Route, [string]$OutputPath) {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes([int]$Schedule.workMinutes)
    $stopAt = $deadline.AddMinutes(5)
    $concurrency = if ($Route -eq 'fresh') {
        "Before changing anything recheck that the original Codex chat remains idle and stop if it became active. Original chat UUID: $($Schedule.threadId). Original rollout: $($Schedule.rollout)."
    } else {
        'You are resuming the original chat. Recheck the current research and World state for independently started work before changing anything.'
    }
    $prompt = @"
The user delegated one scheduled continuation of the existing World Only research in this repository. Read AGENTS.md, docs/observation-runtime.md, docs/world-evolution-workflow.md, and research/WORLD.md for the current authorization, status, and handoff. Stop if the handoff indicates the task is complete or superseded.
$concurrency

Continue one bounded World Only slice using the existing runtime/workflow and the same World. Do not create test Worlds. Preserve user changes and avoid duplicate coordinators, workers, deployments, and active admin changes. Commit your tested changes and use the authorized existing CI/CD push workflow within existing access. Do not change external infrastructure, Proxmox, credentials, or access. The older learning campaign is historical context, so do not start a learning session or restart the safety sweep. Save verified results and next action in research/WORLD.md and report them. Begin handoff before $($deadline.ToString('o')); finish by $($stopAt.ToString('o')). Start changes only when there is enough time to leave a consistent state. No automatic extension or rescheduling.
"@
    $process = [Diagnostics.Process]::new()
    try {
        $process.StartInfo.FileName = @($Schedule.codex)[0]
        $process.StartInfo.WorkingDirectory = $repo
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.CreateNoWindow = $true
        $process.StartInfo.RedirectStandardInput = $true
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        $process.StartInfo.EnvironmentVariables['CODEX_HOME'] = $Schedule.codexHome
        $subcommand = if ($Route -eq 'resume') { ' resume' } else { '' }
        $idArg = if ($Route -eq 'resume') { ' ' + ([Guid]$Schedule.threadId).ToString() } else { '' }
        $process.StartInfo.Arguments = 'exec' + $subcommand + ' --json -c sandbox_mode=\"workspace-write\" -m ' + $Schedule.model + $idArg + ' -'
        $stdoutFile = [IO.File]::Create($OutputPath + '.jsonl')
        try {
            $stderrFile = [IO.File]::Create($OutputPath + '.stderr.log')
            try {
                if (!$process.Start()) { throw 'Unable to start Codex CLI.' }
                $stdout = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
                $stderr = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
                $promptBytes = [Text.Encoding]::UTF8.GetBytes($prompt)
                $process.StandardInput.BaseStream.Write($promptBytes, 0, $promptBytes.Length)
                $process.StandardInput.Close()
                if (!$process.WaitForExit([int][Math]::Max(1, ($stopAt - [DateTimeOffset]::UtcNow).TotalMilliseconds))) {
                    $process.Kill()
                    throw 'Codex wake reached its time limit.'
                }
                if (![Threading.Tasks.Task]::WaitAll(@($stdout, $stderr), 5000)) { throw 'Codex output did not close.' }
                return $process.ExitCode
            } finally { $stderrFile.Dispose() }
        } finally { $stdoutFile.Dispose() }
    } finally { $process.Dispose() }
}

if ($Action -eq 'Register') {
    if ($ThreadId -eq [Guid]::Empty -or $At -le [DateTimeOffset]::Now) { throw 'Provide -ThreadId and a future -At.' }
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw "Task already exists. Inspect $taskName before registering again." }
    $codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
    $rollouts = @(Get-ChildItem -LiteralPath (Join-Path $codexHome 'sessions') -Filter "*-$ThreadId.jsonl" -File -Recurse)
    if ($rollouts.Count -ne 1) { throw 'Unable to uniquely locate the original Codex chat.' }
    $codex = (Get-Command codex.exe -CommandType Application | Select-Object -First 1).Source
    $cache = Get-Content -LiteralPath (Join-Path $codexHome 'models_cache.json') -Raw -ErrorAction SilentlyContinue
    $models = if ($cache) { ($cache | ConvertFrom-Json).models }
    $model = if ($models.slug -contains 'gpt-6.2-astra') { 'gpt-6.2-astra' }
        elseif ($models.slug -contains 'gpt-6-astra') { 'gpt-6-astra' }
        else { 'gpt-6.2-sol' }
    $schedule = @{
        threadId = $ThreadId.ToString()
        rollout = $rollouts[0].FullName
        codex = $codex
        codexHome = $codexHome
        model = $model
        workMinutes = $WorkMinutes
        at = $At.ToUniversalTime().ToString('o')
        until = $At.AddMinutes(55).ToUniversalTime().ToString('o')
    }
    [IO.Directory]::CreateDirectory($stateDir) | Out-Null
    if (Test-Path -LiteralPath $receipt) { throw 'A dispatch receipt already exists. Its prior outputs require review before replacing this schedule.' }
    $schedule | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    $powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $taskAction = New-ScheduledTaskAction -Execute $powershell -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -File "{0}" -Action Run' -f $PSCommandPath) -WorkingDirectory $repo
    $trigger = New-ScheduledTaskTrigger -Once -At $At.LocalDateTime -RepetitionInterval (New-TimeSpan -Minutes 10) -RepetitionDuration (New-TimeSpan -Minutes 51)
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes ($WorkMinutes + 10)) -WakeToRun -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $taskName -Action $taskAction -Trigger $trigger -Principal $principal -Settings $settings -Description 'One bounded Codex continuation of Live World Only research.' | Out-Null
    Log "Registered $taskName for $($At.ToString('o')) with model $model."
    $Action = 'Status'
}

if ($Action -eq 'Status') {
    Get-ScheduledTask -TaskName $taskName | Select-Object TaskName, State | Format-List
    Get-ScheduledTaskInfo -TaskName $taskName | Select-Object NextRunTime, LastRunTime, LastTaskResult | Format-List
    if (Test-Path -LiteralPath $statePath) { Get-Content -LiteralPath $statePath }
    if (Test-Path -LiteralPath (Join-Path $stateDir 'wake.log')) { Get-Content -LiteralPath (Join-Path $stateDir 'wake.log') -Tail 8 }
    return
}

if ($Action -eq 'Disable') {
    [IO.Directory]::CreateDirectory($stateDir) | Out-Null
    [IO.File]::WriteAllText((Join-Path $stateDir 'DISABLED'), '')
    Disable-ScheduledTask -TaskName $taskName | Out-Null
    Log "Disabled future wake attempts for $taskName."
    return
}

$schedule = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if ($Action -eq 'Preview') { Get-ResearchRoute $schedule; return }

try {
    $lease = [IO.File]::Open((Join-Path $stateDir 'dispatch.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
} catch [IO.IOException] {
    Log 'Another wake instance holds the dispatch lock.'
    return
}
try {
    $now = [DateTimeOffset]::UtcNow
    if (($now - [DateTimeOffset]$schedule.at).TotalSeconds -lt -1 -or $now -gt [DateTimeOffset]$schedule.until) {
        Log 'Outside the registered wake window.'
        return
    }
    if ((Test-Path -LiteralPath (Join-Path $stateDir 'DISABLED')) -or (Test-Path -LiteralPath $receipt)) {
        Log 'Wake disabled or already dispatched.'
        return
    }
    $route = Get-ResearchRoute $schedule
    if ($route.StartsWith('defer:')) { Log $route; return }
    $quotaBlock = Get-CodexQuotaBlock $schedule
    if ($quotaBlock) { Log "defer: $quotaBlock"; return }
    $route = Get-ResearchRoute $schedule
    if ($route.StartsWith('defer:')) { Log $route; return }
    $now = [DateTimeOffset]::UtcNow
    if ($now -gt [DateTimeOffset]$schedule.until -or (Test-Path -LiteralPath (Join-Path $stateDir 'DISABLED'))) {
        Log 'Wake expired or was disabled during preflight.'
        return
    }
    $output = Join-Path $stateDir ('codex-' + $now.ToString('yyyyMMddTHHmmssZ'))
    @{ claimedUtc = $now.ToString('o'); route = $route; output = $output } | ConvertTo-Json | Set-Content -LiteralPath $receipt -Encoding UTF8
    Log "Dispatching $route via $($schedule.model)."
    $exitCode = Invoke-Codex $schedule $route $output
    Log "Codex exited with code $exitCode."
    if ($exitCode -ne 0) { exit $exitCode }
} catch {
    Log ('Wake failed: ' + $_.Exception.Message)
    throw
} finally { $lease.Dispose() }

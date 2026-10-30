param([switch]$Capture)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$root = Join-Path $RepoRoot ('artifacts/observation-integration-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$cli = Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
$godot = Get-Godot
function Start-World([string]$Mode, [string]$Config) {
    $directory=Join-Path $root $Mode
    $start=[Diagnostics.ProcessStartInfo]::new($cli); $start.WorkingDirectory=$RepoRoot; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    foreach($argument in @('world','run','--mode',$Mode,'--config',$Config,'--directory',$directory,'--heartbeat-seconds','5')){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(!(Test-Path (Join-Path $directory 'heartbeat.json'))){if($process.HasExited -or $timer.Elapsed.TotalSeconds -gt 30){throw 'World startup failed'}; Start-Sleep -Milliseconds 50}
    return @{ Process=$process; Directory=$directory; Session=(Get-Content (Join-Path $directory 'session-id') -Raw).Trim() }
}
function Invoke-Command([hashtable]$World,[string]$Operation) { & $cli world $Operation --session $World.Session; if($LASTEXITCODE -ne 0){throw "Command failed: $Operation"} }
$worlds=@()
try {
    $world=Start-World 'world-only' 'configs/world-only.json'; $worlds+=$world
    & $godot --headless --audio-driver Dummy --path (Join-Path $RepoRoot 'src/ArtificialLife.Godot') -- --session $world.Session --smoke-frames 10
    if($LASTEXITCODE -ne 0){throw 'Late viewer connection failed'}
    $viewerStart=[Diagnostics.ProcessStartInfo]::new($godot);$viewerStart.UseShellExecute=$false;$viewerStart.CreateNoWindow=$true;$viewerStart.WindowStyle='Hidden'
    foreach($argument in @('--headless','--audio-driver','Dummy','--path',(Join-Path $RepoRoot 'src/ArtificialLife.Godot'),'--','--session',$world.Session)){$viewerStart.ArgumentList.Add($argument)}
    $crashedViewer=[Diagnostics.Process]::Start($viewerStart)
    try { Start-Sleep -Seconds 2; if($crashedViewer.HasExited){throw 'Viewer exited before crash test'}; $crashedViewer.Kill($true);$crashedViewer.WaitForExit() } finally { if(!$crashedViewer.HasExited){$crashedViewer.Kill($true)};$crashedViewer.Dispose() }
    $firstAge=(& $cli world snapshot --session $world.Session | ConvertFrom-Json).state.frame.age
    Start-Sleep -Milliseconds 200
    $secondAge=(& $cli world snapshot --session $world.Session | ConvertFrom-Json).state.frame.age
    if($secondAge -le $firstAge){throw 'World stopped after Viewer crash'}
    Invoke-Command $world 'pause'
    Start-Sleep -Milliseconds 700
    $before=Get-Content (Join-Path $world.Directory 'heartbeat.json') -Raw | ConvertFrom-Json
    Start-Sleep -Milliseconds 1000
    $after=Get-Content (Join-Path $world.Directory 'heartbeat.json') -Raw | ConvertFrom-Json
    if($before.Age -ne $after.Age){throw 'Pause advanced the world'}
    Invoke-Command $world 'resume'
    $worker=(Get-Content (Join-Path $world.Directory 'heartbeat.json') -Raw | ConvertFrom-Json).ProcessId
    [Diagnostics.Process]::GetProcessById($worker).Kill()
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do { Start-Sleep -Milliseconds 100; $next=Get-Content (Join-Path $world.Directory 'heartbeat.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json; if($timer.Elapsed.TotalSeconds -gt 20){throw 'Worker restart failed'} } while(!$next -or $next.ProcessId -eq $worker)
    & $godot --headless --audio-driver Dummy --path (Join-Path $RepoRoot 'src/ArtificialLife.Godot') -- --session $world.Session --smoke-frames 10
    if($LASTEXITCODE -ne 0){throw 'Viewer reconnect failed'}
    if($Capture) { & $godot --audio-driver Dummy --path (Join-Path $RepoRoot 'src/ArtificialLife.Godot') -- --session $world.Session --capture (Join-Path $root 'world.png'); if($LASTEXITCODE -ne 0){throw 'Capture failed'} }
    $learning=Start-World 'learning' 'configs/runtime.json'; $worlds+=$learning
    & $godot --headless --audio-driver Dummy --path (Join-Path $RepoRoot 'src/ArtificialLife.Godot') -- --session $learning.Session --smoke-frames 10
    if($LASTEXITCODE -ne 0){throw 'Learning viewer failed'}
    Invoke-Command $learning 'pause'
    Invoke-Command $learning 'evaluation'
    Start-Sleep -Milliseconds 300
    $learningPid=(Get-Content (Join-Path $learning.Directory 'heartbeat.json') -Raw | ConvertFrom-Json).ProcessId
    $registry=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Live/sessions/'+$learningPid+'.json')
    $endpoint=(Get-Content $registry -Raw | ConvertFrom-Json).Endpoint
    $sessions=Invoke-RestMethod ($endpoint+'/sessions')
    $child=@($sessions | Where-Object { $_.mode -eq 'Evaluation' -and $_.parentId -eq $learning.Session })
    if($child.Count -ne 1 -or $child[0].sessionId -eq $learning.Session){throw 'Evaluation did not create an independent observable child'}
    Invoke-Command $learning 'evaluation'
    Invoke-Command $learning 'resume'
    $learningWorker=(Get-Content (Join-Path $learning.Directory 'heartbeat.json') -Raw | ConvertFrom-Json).ProcessId
    [Diagnostics.Process]::GetProcessById($learningWorker).Kill()
    if(!$learning.Process.WaitForExit(15000) -or $learning.Process.ExitCode -eq 0){throw 'Lost Learning state was silently resumed'}
    if(!(Select-String -LiteralPath (Join-Path $learning.Directory 'events.jsonl') -SimpleMatch 'learning-state-lost')){throw 'Missing explicit Learning state-loss event'}
    @{Passed=$true;Checks=@('late-join','viewer-crash-isolation','pause-heartbeat','resume','worker-recovery','reconnect','multiple-worlds','learning','isolated-evaluation','learning-loss-is-failure','godot');Artifacts=$root} | ConvertTo-Json | Set-Content (Join-Path $root 'summary.json')
    Write-Output "OBSERVATION_SMOKE_OK $root"
} finally {
    foreach($world in $worlds) { if(!$world.Process.HasExited) { & $cli world stop --directory $world.Directory; if(!$world.Process.WaitForExit(10000)){$world.Process.Kill($true)} }; $world.Process.Dispose() }
}

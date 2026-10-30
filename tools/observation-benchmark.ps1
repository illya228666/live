param([int]$Steps=30000,[int]$Repetitions=3)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$root=Join-Path $RepoRoot ('artifacts/observation-benchmark-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$cli=Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
$godot=Get-Godot
$rows=@()
$oldOmp=$env:OMP_NUM_THREADS; $oldMkl=$env:MKL_NUM_THREADS
$env:OMP_NUM_THREADS='1';$env:MKL_NUM_THREADS='1'
function Start-Hidden([string]$Executable,[string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.WorkingDirectory=$RepoRoot
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    return [Diagnostics.Process]::Start($start)
}
try {
    for($iteration=0;$iteration -lt $Repetitions;$iteration++) {
        foreach($client in @('none','viewer','unread')) {
            $directory=Join-Path $root ($client+'-'+$iteration)
            $world=Start-Hidden $cli @('world','run','--mode','learning','--config','configs/runtime.json','--directory',$directory,'--steps',"$Steps",'--checkpoint-seconds','300')
            $viewer=$null;$socket=$null
            try {
                $timer=[Diagnostics.Stopwatch]::StartNew()
                while(!(Test-Path (Join-Path $directory 'heartbeat.json'))){if($world.HasExited -or $timer.Elapsed.TotalSeconds -gt 30){throw 'Benchmark world startup failed'};Start-Sleep -Milliseconds 20}
                $session=(Get-Content (Join-Path $directory 'session-id') -Raw).Trim()
                if($client -eq 'viewer') { $viewer=Start-Hidden $godot @('--audio-driver','Dummy','--path',(Join-Path $RepoRoot 'src/ArtificialLife.Godot'),'--','--session',$session) }
                if($client -eq 'unread') {
                    $workerPid=(Get-Content (Join-Path $directory 'heartbeat.json') -Raw | ConvertFrom-Json).ProcessId
                    $registry=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Live/sessions/'+$workerPid+'.json')
                    $endpoint=$null
                    $connectTimer=[Diagnostics.Stopwatch]::StartNew()
                    while(!$endpoint) {
                        if($world.HasExited -or $connectTimer.Elapsed.TotalSeconds -gt 10){throw 'Worker observation endpoint did not become available'}
                        try {
                            $record=Get-Content $registry -Raw -ErrorAction Stop | ConvertFrom-Json
                            $registered=Invoke-RestMethod ($record.Endpoint+'/sessions') -TimeoutSec 1
                            if(@($registered | Where-Object sessionId -eq $session).Count -gt 0){$endpoint=$record.Endpoint}
                        } catch { }
                        if(!$endpoint){Start-Sleep -Milliseconds 50}
                    }
                    $socket=[Net.WebSockets.ClientWebSocket]::new()
                    $null=$socket.ConnectAsync([Uri]($endpoint.Replace('http://','ws://')+'/observe/'+$session+'?version=1'),[Threading.CancellationToken]::None).GetAwaiter().GetResult()
                }
                if(!$world.WaitForExit(120000)){throw 'Benchmark world timeout'}
                if($world.ExitCode -ne 0){throw 'Benchmark world failed'}
                $performance=Get-Content (Join-Path $directory 'performance.json') -Raw | ConvertFrom-Json
                $pointer=Get-Content (Join-Path $directory 'policy/current') -Raw | ConvertFrom-Json
                $manifest=Get-Content (Join-Path $directory ('policy/'+$pointer.Generation+'/manifest.json')) -Raw | ConvertFrom-Json
                if($performance.PSObject.Properties.Name -contains 'ObservationFrames') {
                    if($client -eq 'none' -and $performance.ObservationFrames -ne 0){throw 'Unobserved world prepared network frames'}
                    if($client -ne 'none' -and $performance.ObservationFrames -eq 0){throw 'Observer did not attach'}
                }
                $rows+= [pscustomobject]@{Client=$client;Iteration=$iteration;Steps=$performance.Steps;Seconds=$performance.Seconds;StepsPerSecond=$performance.StepsPerSecond;WeightSha256=$manifest.WeightSha256;ObservationFrames=$performance.ObservationFrames;ManagedBytes=$performance.ManagedBytes;PeakWorkingSetBytes=$performance.PeakWorkingSetBytes}
                Write-Output ($client+' '+$iteration+' '+[Math]::Round($performance.StepsPerSecond)+' steps/s')
            } finally {
                if($socket){$socket.Abort();$socket.Dispose()}
                if($viewer){if(!$viewer.HasExited){$viewer.Kill($true);$viewer.WaitForExit()};$viewer.Dispose()}
                if(!$world.HasExited){$world.Kill($true);$world.WaitForExit()};$world.Dispose()
            }
        }
    }
    $medians=@{}
    foreach($client in @('none','viewer','unread')){$values=@($rows | Where-Object Client -eq $client | Sort-Object StepsPerSecond);$medians[$client]=$values[[int][Math]::Floor($values.Count/2)].StepsPerSecond}
    $report=@{Steps=$Steps;Repetitions=$Repetitions;CpuThreads=1;Runs=$rows;MedianStepsPerSecond=$medians;ViewerOverheadPercent=100*(1-$medians.viewer/$medians.none);UnreadOverheadPercent=100*(1-$medians.unread/$medians.none);IdenticalWeights=(@($rows.WeightSha256 | Select-Object -Unique).Count -eq 1)}
    $report | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $root 'summary.json')
    $root | Set-Content (Join-Path $RepoRoot 'artifacts/last-observation-benchmark.txt')
    if(!$report.IdenticalWeights){throw 'Observation changed trained weights'}
    if($report.ViewerOverheadPercent -gt 5 -or $report.UnreadOverheadPercent -gt 5){throw "Observation overhead exceeds the 5 percent target; inspect summary.json"}
    Write-Output ('OBSERVATION_BENCHMARK_OK '+$root+' viewer overhead='+[Math]::Round($report.ViewerOverheadPercent,2)+'% unread='+[Math]::Round($report.UnreadOverheadPercent,2)+'%')
} finally {$env:OMP_NUM_THREADS=$oldOmp;$env:MKL_NUM_THREADS=$oldMkl}

[CmdletBinding()]
param([string]$Directory = ('artifacts/world-smoke-' + [Guid]::NewGuid().ToString('N')))
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
Invoke-Dotnet build src/ArtificialLife.Cli -c Release --no-restore --verbosity quiet
Invoke-Dotnet build tests/ArtificialLife.Tests -c Release --no-restore --verbosity quiet
$worldRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot $Directory))
New-Item -ItemType Directory -Path $worldRoot -Force | Out-Null
$cli = Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
$fixture = Join-Path $RepoRoot 'tests/ArtificialLife.Tests/bin/Release/net10.0/ArtificialLife.Tests.dll'
function Publish-Fixture([string]$Version, [int]$FailAfter = 0, [int]$HangAfter = 0) {
    $folder = Join-Path $worldRoot ('source/' + $Version)
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Copy-Item -LiteralPath $fixture -Destination $folder
    @{ id='world-fixture'; version=$Version; apiVersion=1; supportsWorldOnly=$true; assembly='ArtificialLife.Tests.dll'; entryType='ArtificialLife.Tests.WorldEvolutionFixture'; logicHash=(Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash; settings=@{failAfter=$FailAfter;hangAfter=$HangAfter} } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $folder 'module.json') -Encoding UTF8
    return $folder
}
function Send-World([string]$Operation, [string]$Id, [string]$Source = '') {
    $arguments = @('world',$Operation,'--directory',$worldRoot,'--id',$Id)
    if ($Source) { $arguments += @('--sources',$Source) }
    & $cli @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw ('Command submission failed: ' + $Id) }
}
function Wait-Result([string]$Id, [bool]$Expected = $true) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $path = Join-Path $worldRoot ('commands/' + $Id + '.result.json')
    while (!(Test-Path -LiteralPath $path)) {
        if ($worker.HasExited -or $watch.Elapsed.TotalSeconds -gt 90) { throw ('Result timeout: ' + $Id) }
        Start-Sleep -Milliseconds 100
    }
    $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($result.Accepted -ne $Expected) { throw ($Id + ': ' + $result.Message) }
    return $result
}
function Wait-State([scriptblock]$Predicate) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $state = Get-Content -LiteralPath (Join-Path $worldRoot 'recovery.json') -Raw | ConvertFrom-Json
        if (& $Predicate $state) { return $state }
        if ($worker.HasExited -or $watch.Elapsed.TotalSeconds -gt 90) { throw 'Recovery timeout.' }
        Start-Sleep -Milliseconds 100
    }
}
$worker = Start-Process -FilePath $cli -ArgumentList @('world','run','--directory',('"'+$worldRoot+'"'),'--config','configs/world-only.json','--probation-steps','2000','--checkpoint-seconds','2','--heartbeat-seconds','5','--validation-seconds','3') -WorkingDirectory $RepoRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $worldRoot 'stdout.log') -RedirectStandardError (Join-Path $worldRoot 'stderr.log') -PassThru
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path (Join-Path $worldRoot 'observation.json'))) { if ($worker.HasExited -or $watch.Elapsed.TotalSeconds -gt 20) { throw 'Startup failed.' }; Start-Sleep -Milliseconds 100 }
    $duplicate = Start-Process -FilePath $cli -ArgumentList @('world','run','--directory',('"'+$worldRoot+'"'),'--steps','1') -WorkingDirectory $RepoRoot -WindowStyle Hidden -RedirectStandardError (Join-Path $worldRoot 'duplicate-error.log') -PassThru
    if (!$duplicate.WaitForExit(10000) -or $duplicate.ExitCode -eq 0) { throw 'Duplicate owner was not rejected.' }
    $corrupt = Publish-Fixture 'corrupt'
    Add-Content -LiteralPath (Join-Path $corrupt 'ArtificialLife.Tests.dll') -Value 'corruption'
    Send-World 'update' 'corrupt' $corrupt
    Wait-Result 'corrupt' $false | Out-Null
    Send-World 'update' 'validation-hang' (Publish-Fixture 'hang-test' 0 1)
    Wait-Result 'validation-hang' $false | Out-Null
    Send-World 'update' 'add' (Publish-Fixture '1')
    Wait-Result 'add' | Out-Null
    Wait-State { param($s) !$s.Pending } | Out-Null
    Send-World 'update' 'broken' (Publish-Fixture '2' 1100)
    Wait-Result 'broken' | Out-Null
    Wait-State { param($s) @($s.Rejected).Count -gt 0 -and !$s.Pending } | Out-Null
    Send-World 'update' 'live-hang' (Publish-Fixture 'hang-live' 0 1100)
    Wait-Result 'live-hang' | Out-Null
    Wait-State { param($s) @($s.Rejected).Count -ge 2 -and !$s.Pending } | Out-Null
    Send-World 'update' 'fixed' (Publish-Fixture '3')
    Wait-Result 'fixed' | Out-Null
    Wait-State { param($s) !$s.Pending } | Out-Null
    Send-World 'checkpoint' 'before-crash'
    Wait-Result 'before-crash' | Out-Null
    $heartbeat = Get-Content -LiteralPath (Join-Path $worldRoot 'heartbeat.json') -Raw | ConvertFrom-Json
    $crashedProcess = Get-Process -Id $heartbeat.ProcessId
    $crashedProcess.Kill()
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $replacement = Get-Content -LiteralPath (Join-Path $worldRoot 'heartbeat.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json
        if ($watch.Elapsed.TotalSeconds -gt 20) { throw 'Crash recovery timeout.' }
    } while (!$replacement -or $replacement.ProcessId -eq $heartbeat.ProcessId)
    Send-World 'rollback' 'rollback'
    Wait-Result 'rollback' | Out-Null
    Start-Sleep -Milliseconds 500
    Send-World 'update' 'after-rollback' (Publish-Fixture '5')
    Wait-Result 'after-rollback' | Out-Null
    Wait-State { param($s) !$s.Pending } | Out-Null
    Send-World 'screenshot' 'capture' 
    $captureResult = Wait-Result 'capture'
    Send-World 'checkpoint' 'saved'
    Wait-Result 'saved' | Out-Null
    # A repeated request must return the original result without applying the module again.
    Send-World 'update' 'fixed' (Publish-Fixture '4')
    $again = Wait-Result 'fixed'
    if ($again.Message -notlike 'Activated*') { throw 'Idempotency failed.' }
    Send-World 'stop' 'stop'
    Wait-Result 'stop' | Out-Null
    if (!$worker.WaitForExit(10000) -or $worker.ExitCode -ne 0) { throw 'Graceful stop failed.' }
    $observation = Get-Content -LiteralPath (Join-Path $worldRoot 'observation.json') -Raw | ConvertFrom-Json
    if ($observation.Frame.Age -lt 100000) { throw 'Smoke did not reach 100000 steps.' }
    if (@($observation.Packages | Where-Object { $_.Id -eq 'world-fixture' -and $_.Version -eq '5' }).Count -ne 1) { throw 'Fixed package is not active.' }
    @{passed=$true;age=$observation.Frame.Age;revision=$observation.Frame.Revision;capture=$captureResult.Artifact;checks=@('corruption','validation-timeout','single-owner','late-exception','late-hang','watchdog-rollback','process-recovery','manual-rollback','corrected-update','idempotency','json','png')} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $worldRoot 'smoke-summary.json') -Encoding UTF8
    Write-Output ('WORLD_SMOKE_OK age=' + $observation.Frame.Age + ' artifacts=' + $worldRoot + ' png=' + $captureResult.Artifact)
}
finally { if (!$worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() } }

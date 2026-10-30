param()
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$root=Join-Path $RepoRoot ('artifacts/visual-smoke-'+[Guid]::NewGuid().ToString('N'))
$cli=Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
$godot=Get-Godot
$fixture=Join-Path $RepoRoot 'tests/ArtificialLife.Tests/bin/Release/net10.0/ArtificialLife.Tests.dll'
New-Item -ItemType Directory -Path $root | Out-Null
function Start-Hidden([string]$Executable,[string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new($Executable);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden';$start.WorkingDirectory=$RepoRoot
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    return [Diagnostics.Process]::Start($start)
}
foreach($version in @('1','2','3','4')) {
    $folder=Join-Path $root ('packages/visual-probe/'+$version)
    New-Item -ItemType Directory -Path $folder | Out-Null
    Copy-Item -LiteralPath $fixture -Destination $folder
    $manifest=@{id='visual-probe';version=$version;apiVersion=1;supportsWorldOnly=$true;assembly='ArtificialLife.Tests.dll';entryType='ArtificialLife.Tests.VisualProbeFixture';logicHash=(Get-FileHash $fixture -Algorithm SHA256).Hash;schemas=@{'visual-probe.entity'=1};settings=@{};visuals=@(@{key='probe';component='visual-probe.entity';scene=('res://visual-probe/'+$version+'/creature.tscn');layer=75;scale=2;bindings=@(@{property='rotation';component='visual-probe.entity';field='heading'})})}
    if($version -in @('1','2')) {
        $source=Join-Path $root ('resources/'+$version)
        New-Item -ItemType Directory -Path $source | Out-Null
        Copy-Item -Path (Join-Path $RepoRoot 'src/ArtificialLife.Godot/visual-foundation/*') -Destination $source
        if($version -eq '2') { $scene=Join-Path $source 'creature.tscn'; (Get-Content $scene -Raw).Replace('0.4, 0.87, 0.74','0.95, 0.4, 0.55') | Set-Content $scene }
        & (Join-Path $PSScriptRoot 'pack-visuals.ps1') -Source $source -Output (Join-Path $folder 'visual.pck') -Prefix ('visual-probe/'+$version)
        $manifest.presentationFiles=@{'visual.pck'=(Get-FileHash (Join-Path $folder 'visual.pck') -Algorithm SHA256).Hash}
    } else {
        # Graphics defects must leave validation, activation and physics usable.
        $manifest.presentationFiles=@{'missing.pck'=('0'*64)}
        if($version -eq '4') { $manifest.presentationAssembly='ArtificialLife.Tests.dll';$manifest.presentationHash=$manifest.logicHash;$manifest.presentationType='Missing.Presentation' }
    }
    $manifest | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $folder 'module.json')
}
$worldDirectory=Join-Path $root 'world'
@{mode='WorldOnly';packageRoot=(Join-Path $root 'packages');packages=@(@{id='visual-probe';version='1'})} | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $root 'config.json')
$world=Start-Hidden $cli @('world','run','--config',(Join-Path $root 'config.json'),'--directory',$worldDirectory,'--probation-steps','100')
$watch=[Diagnostics.Stopwatch]::StartNew();$viewer=$null
try {
    while(!(Test-Path (Join-Path $worldDirectory 'heartbeat.json'))){if($world.HasExited -or $watch.Elapsed.TotalSeconds -gt 20){throw 'Visual world startup failed'};Start-Sleep -Milliseconds 50}
    $session=(Get-Content (Join-Path $worldDirectory 'session-id') -Raw).Trim()
    $snapshot=& $cli world snapshot --session $session | ConvertFrom-Json
    $revision=$snapshot.state.frame.revision
    if(@($snapshot.state.frame.entities | Where-Object {$_.components.'visual-probe.entity'}).Count -ne 1){throw 'Probe entity is absent'}
    # This Viewer remains connected while version 2 is activated.
    $viewer=Start-Hidden $godot @('--headless','--audio-driver','Dummy','--path',(Join-Path $RepoRoot 'src/ArtificialLife.Godot'),'--','--session',$session,'--smoke-frames','10','--smoke-metadata','2')
    foreach($version in @('2','3','4')) {
        if($version -eq '2'){Start-Sleep -Seconds 1}
        & (Join-Path $PSScriptRoot 'activate.ps1') -Session $session -Sources (Join-Path $root ('packages/visual-probe/'+$version))
        $wait=[Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 100
            $snapshot=& $cli world snapshot --session $session | ConvertFrom-Json
            if($world.HasExited -or $wait.Elapsed.TotalSeconds -gt 25){throw 'Visual-only update did not activate'}
        } while(@($snapshot.state.packages | Where-Object {$_.id -eq 'visual-probe' -and $_.version -eq $version}).Count -ne 1)
        if($snapshot.state.frame.revision -ne $revision){throw 'Visual-only update changed training conditions'}
        if($version -eq '2') {
            if(!$viewer.WaitForExit(15000) -or $viewer.ExitCode -ne 0){throw 'Connected Viewer did not refresh visual resources'}
            & $cli world screenshot --session $session --target (Join-Path $root 'updated.png')
            if($LASTEXITCODE -ne 0){throw 'PCK screenshot failed'}
        } else {
            & $godot --headless --audio-driver Dummy --path (Join-Path $RepoRoot 'src/ArtificialLife.Godot') -- --session $session --smoke-frames 10
            if($LASTEXITCODE -ne 0){throw 'Viewer fallback failed'}
        }
    }
    @{Passed=$true;Checks=@('new-module','pck','connected-resource-update','unchanged-conditions','missing-resource-fallback','presentation-exception-fallback','capture')} | ConvertTo-Json | Set-Content (Join-Path $root 'summary.json')
    Write-Output ('VISUAL_SMOKE_OK '+$root)
} finally {
    if($viewer){if(!$viewer.HasExited){$viewer.Kill($true)};$viewer.Dispose()}
    if(!$world.HasExited){& $cli world stop --directory $worldDirectory;if(!$world.WaitForExit(10000)){$world.Kill($true)}};$world.Dispose()
}

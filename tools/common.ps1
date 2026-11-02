$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ToolVersions = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'versions.json') -Raw | ConvertFrom-Json
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$nugetRoot = Join-Path $RepoRoot '.tools/nuget'
$env:NUGET_PACKAGES = Join-Path $nugetRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $nugetRoot 'http-cache'
$env:NUGET_PLUGINS_CACHE_PATH = Join-Path $nugetRoot 'plugins-cache'
$env:NUGET_SCRATCH = Join-Path $nugetRoot 'scratch'

function Initialize-Dotnet {
    $local = Join-Path $RepoRoot (Join-Path $ToolVersions.dotnet.localDirectory 'dotnet.exe')
    if (!(Test-Path -LiteralPath $local)) {
        throw "Run ./tools/bootstrap.ps1 first: repository-local .NET SDK $($ToolVersions.dotnet.version) is missing."
    }
    $script:Dotnet = $local
    $env:DOTNET_ROOT = Split-Path $local
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    Push-Location $RepoRoot
    try {
        $version = & $script:Dotnet --version
        if ($LASTEXITCODE -ne 0 -or $version -ne $ToolVersions.dotnet.version) {
            throw "Run ./tools/bootstrap.ps1 first: repository-local SDK $($ToolVersions.dotnet.version) is required."
        }
    }
    finally { Pop-Location }
}

function Invoke-Dotnet {
    Push-Location $RepoRoot
    try {
        & $script:Dotnet @args
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

function Get-Godot {
    $local = Join-Path $RepoRoot (Join-Path '.tools/godot' $ToolVersions.godot.consoleExecutable)
    if (Test-Path -LiteralPath $local) { return $local }
    $command = Get-Command godot -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $version = & $command.Source --version
        if ($LASTEXITCODE -eq 0 -and $version -like "$($ToolVersions.godot.version).stable.mono.*") { return $command.Source }
    }
    throw 'Pinned Godot .NET is missing. Run ./tools/bootstrap.ps1.'
}

# Control commands may use a dev CLI. Long-lived processes must use a pinned release.
function Get-LiveCli([string]$Cli='', [switch]$Pinned) {
    if ($Cli) { $candidate=[IO.Path]::GetFullPath($Cli) }
    elseif ($Pinned) {
        $pointer=Join-Path $RepoRoot 'artifacts/hosts/current.json'
        if (!(Test-Path -LiteralPath $pointer)) { throw 'Publish a Host first: ./tools/host.ps1 -Action publish' }
        $candidate=(Get-Content -LiteralPath $pointer -Raw | ConvertFrom-Json).cli
    } else { $candidate=Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe' }
    if (!(Test-Path -LiteralPath $candidate)) { throw ('CLI is missing: '+$candidate) }
    if ($Pinned) {
        $releaseRoot=Split-Path $candidate
        $manifestPath=Join-Path $releaseRoot 'host.json'
        if (!(Test-Path -LiteralPath $manifestPath)) {
            throw 'Running Host must use a published release, not dev bin. Publish with host.ps1 and select -Release or -HostRelease explicitly for a legacy World.'
        }
        $manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        foreach ($file in $manifest.files.GetEnumerator()) {
            if ((Get-FileHash -LiteralPath (Join-Path $releaseRoot $file.Key)).Hash -ne $file.Value) {
                throw ('Host release changed: '+$file.Key)
            }
        }
    }
    return $candidate
}

function Write-LiveJson([string]$Path, $Value) {
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($Path))) -Force | Out-Null
    $temporary=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    $Value | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Invoke-LiveJson([string]$Cli, [string[]]$Arguments) {
    $json=& $Cli @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('CLI command failed: '+($Arguments -join ' ')) }
    return ($json -join "`n") | ConvertFrom-Json -Depth 100
}

function Wait-LiveResult([string]$Directory, [string]$Id, [int]$TimeoutSeconds=120) {
    $path=Join-Path $Directory ('commands/'+$Id+'.result.json')
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $path)) {
        if ($watch.Elapsed.TotalSeconds -gt $TimeoutSeconds) { throw ('Command completion timeout: '+$Id) }
        Start-Sleep -Milliseconds 200
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-LiveSnapshot([string]$Cli, [string]$Session) {
    return Invoke-LiveJson $Cli @('world','snapshot','--session',$Session)
}

function Start-LiveHost([string]$Cli, [string]$Directory, [string[]]$Arguments=@()) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $nativeArguments=@('world','run','--directory',$Directory)+$Arguments
    $quoted=@($nativeArguments | ForEach-Object { '"'+$_.Replace('"','\"')+'"' })
    $logPrefix=Join-Path $Directory ('host-'+[Guid]::NewGuid().ToString('N'))
    return Start-Process -FilePath $Cli -ArgumentList $quoted -WorkingDirectory $RepoRoot -WindowStyle Hidden `
        -RedirectStandardOutput ($logPrefix+'.stdout.log') -RedirectStandardError ($logPrefix+'.stderr.log') -PassThru
}

function Wait-LiveHost([string]$Cli, [string]$Directory, $Process, [int]$TimeoutSeconds=45) {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -le $TimeoutSeconds) {
        if ($Process.HasExited) { throw ('Host exited during startup: '+$Process.ExitCode) }
        $identity=Join-Path $Directory 'session-id'
        if (Test-Path -LiteralPath $identity) {
            try {
                $snapshot=Get-LiveSnapshot $Cli (Get-Content -LiteralPath $identity -Raw).Trim() 2>$null
                if ($snapshot.session.status -ne 'running') { Start-Sleep -Milliseconds 200; continue }
                if ($snapshot.state.fault) { throw $snapshot.state.fault }
                $heartbeatPath=Join-Path $Directory 'heartbeat.json'
                if (!(Test-Path -LiteralPath $heartbeatPath)) { Start-Sleep -Milliseconds 200; continue }
                $heartbeat=Get-Content -LiteralPath $heartbeatPath -Raw | ConvertFrom-Json
                $worker=Get-CimInstance Win32_Process -Filter "ProcessId=$($heartbeat.ProcessId)"
                if (!$worker -or $worker.ParentProcessId -ne $Process.Id) { Start-Sleep -Milliseconds 200; continue }
                return $snapshot
            } catch { Start-Sleep -Milliseconds 200 }
        } else { Start-Sleep -Milliseconds 200 }
    }
    throw 'Host did not become observable before the startup timeout.'
}

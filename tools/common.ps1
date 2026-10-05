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
    $local = Join-Path $RepoRoot '.tools/dotnet/dotnet.exe'
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

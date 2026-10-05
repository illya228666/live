[CmdletBinding()]
param([switch]$Headless, [switch]$ForceLocal)
. (Join-Path $PSScriptRoot 'common.ps1')

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Bootstrap downloads Windows tools. On Linux use the pinned SDK and dotnet restore/build/test (see README).'
}
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($architecture -ne 'X64') {
    throw "Detected $architecture. This MVP requires Windows x64: upstream LibTorch CPU has no Windows ARM64/x86 package. No incompatible binaries were installed."
}
Write-Host "ArtificialLife bootstrap: Windows $architecture (repository-local tools)"
$downloads = Join-Path $RepoRoot '.tools/downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null

function Install-Archive($pin, [string]$fileName, [string]$destination) {
    $archive = Join-Path $downloads $fileName
    if (Test-Path -LiteralPath $archive) {
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant() -ne $pin.sha512) {
            Remove-Item -LiteralPath $archive
        }
    }
    if (!(Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading $fileName from official distribution..."
        $partial = "$archive.partial"
        $oldProgress = $ProgressPreference
        try {
            $ProgressPreference = 'SilentlyContinue'
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Uri $pin.url -OutFile $partial
            if ((Get-FileHash -LiteralPath $partial -Algorithm SHA512).Hash.ToLowerInvariant() -ne $pin.sha512) {
                throw "SHA512 mismatch: $fileName"
            }
            Move-Item -LiteralPath $partial -Destination $archive -Force
        }
        finally { $ProgressPreference = $oldProgress }
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
}

$globalSdk = $false
if (!$ForceLocal) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $sdks = & $command.Source --list-sdks
        $globalSdk = @($sdks | Where-Object { $_ -match "^$([regex]::Escape($ToolVersions.dotnet.version)) " }).Count -gt 0
    }
}
$localDotnet = Join-Path $RepoRoot '.tools/dotnet/dotnet.exe'
$validLocal = $false
if (Test-Path -LiteralPath $localDotnet) {
    Push-Location $RepoRoot
    try { $validLocal = (& $localDotnet --version) -eq $ToolVersions.dotnet.version -and $LASTEXITCODE -eq 0 }
    finally { Pop-Location }
}
if (!$validLocal -and ((Test-Path -LiteralPath $localDotnet) -or !$globalSdk -or $ForceLocal)) {
    Install-Archive $ToolVersions.dotnet 'dotnet.zip' (Join-Path $RepoRoot '.tools/dotnet')
}
Initialize-Dotnet
if (!$Headless) {
    $godot = $null
    if (!$ForceLocal) {
        try { $godot = Get-Godot } catch { $godot = $null }
    }
    $localGodot = Join-Path $RepoRoot (Join-Path '.tools/godot' $ToolVersions.godot.consoleExecutable)
    if ($null -eq $godot -and !(Test-Path -LiteralPath $localGodot)) {
        Install-Archive $ToolVersions.godot 'godot.zip' (Join-Path $RepoRoot '.tools/godot')
    }
    $godot = Get-Godot
    $version = & $godot --version
    if ($LASTEXITCODE -ne 0 -or $version -notlike "$($ToolVersions.godot.version).stable.mono.*") { throw 'Godot version verification failed.' }
    Write-Host "Godot: $version"
}
Invoke-Dotnet restore ArtificialLife.sln --force
# Check the native tensor backend, not just NuGet restore.
Invoke-Dotnet test tests/ArtificialLife.Tests -c Release --filter 'FullyQualifiedName~EncoderAndScorerAcceptVariableSets' --verbosity minimal
Write-Host "Ready: SDK $($ToolVersions.dotnet.version). No global software was installed."
Write-Host './tools/build.ps1'
Write-Host './tools/test.ps1'
Write-Host './tools/train.ps1'
if (!$Headless) { Write-Host './tools/run-godot.ps1' }

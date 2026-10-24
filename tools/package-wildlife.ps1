[CmdletBinding()]
param([string]$Version='1.0.0', [string]$OutputRoot='artifacts/packages')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid wildlife package version.' }
$wildlifeRoot = if ([IO.Path]::IsPathRooted($OutputRoot)) { [IO.Path]::GetFullPath($OutputRoot) } else { Join-Path $RepoRoot $OutputRoot }
$wildlifeDestination = Join-Path $wildlifeRoot ('wildlife/' + $Version)
if (Test-Path -LiteralPath $wildlifeDestination) { throw "Published package already exists: $wildlifeDestination. Choose a new version." }
$wildlifeLogic = 'ArtificialLife.Modules.Wildlife'
$wildlifeView = 'ArtificialLife.Modules.Wildlife.Presentation.Godot'
Invoke-Dotnet build ('src/' + $wildlifeLogic) -c Release --verbosity quiet
Invoke-Dotnet build ('src/' + $wildlifeView) -c Release --verbosity quiet
$wildlifeStage = Join-Path $wildlifeRoot ('wildlife/.staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $wildlifeStage 'assets') -Force | Out-Null
foreach ($wildlifeProject in @($wildlifeLogic, $wildlifeView)) {
    $wildlifeBin = if ($wildlifeProject -eq $wildlifeLogic) { 'bin/Release/net10.0' } else { '.godot/mono/temp/bin/Release' }
    foreach ($wildlifeExtension in @('.dll', '.deps.json')) {
        Copy-Item -LiteralPath (Join-Path $RepoRoot ('src/' + $wildlifeProject + '/' + $wildlifeBin + '/' + $wildlifeProject + $wildlifeExtension)) -Destination $wildlifeStage
    }
}
Copy-Item -LiteralPath (Join-Path $RepoRoot 'assets/wildlife/boar.png') -Destination (Join-Path $wildlifeStage 'assets/boar.png')
function Get-WildlifeHash([string]$Relative) { (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $wildlifeStage $Relative)).Hash }
$wildlifeManifest = [ordered]@{
    id='wildlife'; version=$Version; apiVersion=1; stateSchemaVersion=1
    assembly=($wildlifeLogic + '.dll'); entryType=($wildlifeLogic + '.RuntimeWildlife')
    logicHash=(Get-WildlifeHash ($wildlifeLogic + '.dll'))
    logicFiles=@{ ($wildlifeLogic + '.deps.json')=(Get-WildlifeHash ($wildlifeLogic + '.deps.json')) }
    requires=@('vision.appearance', 'motivation'); provides=@('wildlife', 'safety')
    schemas=@{ 'wildlife.boar'=1 }
    settings=[ordered]@{ initialCount=1; speedFactor=0.75; minTurnSeconds=3; maxTurnSeconds=8; turnDegreesPerSecond=45; dangerRadius=10; driveWeight=1; minSpawnDistance=15; diameter=4 }
    presentationAssembly=($wildlifeView + '.dll'); presentationType=($wildlifeView + '.BoarPresentation'); presentationOrder=75
    presentationHash=(Get-WildlifeHash ($wildlifeView + '.dll'))
    presentationFiles=@{ ($wildlifeView + '.deps.json')=(Get-WildlifeHash ($wildlifeView + '.deps.json')); 'assets/boar.png'=(Get-WildlifeHash 'assets/boar.png') }
}
$wildlifeManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $wildlifeStage 'module.json') -Encoding UTF8
# Publishing is one directory rename; an existing version is never overwritten.
[IO.Directory]::Move($wildlifeStage, $wildlifeDestination)
Write-Output "Wildlife package prepared (not activated): $wildlifeDestination"

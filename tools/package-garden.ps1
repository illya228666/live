[CmdletBinding()]
param([ValidateSet('flora')][string]$Mechanic = 'flora', [string]$Version = '1.0.0',
      [string]$OutputRoot = 'artifacts/evolution-20261102/packages')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { throw 'Invalid package version.' }
$gardenDestination = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ($Mechanic + '/' + $Version)
if (Test-Path -LiteralPath $gardenDestination) { throw 'Published versions are immutable. Choose a new version.' }
Invoke-Dotnet build src/ArtificialLife.Modules.Garden -c Release --verbosity quiet
$gardenStage = Join-Path (Split-Path $gardenDestination) ('.staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $gardenStage -Force | Out-Null
$gardenAssembly = 'ArtificialLife.Modules.Garden'
foreach ($extension in @('.dll', '.deps.json')) {
    Copy-Item -LiteralPath (Join-Path $RepoRoot ('src/' + $gardenAssembly + '/bin/Release/net10.0/' + $gardenAssembly + $extension)) -Destination $gardenStage
}
& (Join-Path $PSScriptRoot 'pack-visuals.ps1') -Source assets/garden -Output (Join-Path $gardenStage 'garden.pck') -Prefix ($Mechanic + '/' + $Version)
function Garden-Hash([string]$File) { (Get-FileHash -LiteralPath (Join-Path $gardenStage $File) -Algorithm SHA256).Hash }
$gardenManifest = [ordered]@{
    id=$Mechanic; version=$Version; apiVersion=1; supportsWorldOnly=$true
    assembly=($gardenAssembly + '.dll'); entryType=($gardenAssembly + '.RuntimeFlora')
    logicHash=(Garden-Hash ($gardenAssembly + '.dll'))
    logicFiles=@{ ($gardenAssembly + '.deps.json')=(Garden-Hash ($gardenAssembly + '.deps.json')) }
    provides=@('flora'); requires=@('nutrition','vision.appearance')
    schemas=@{'flora.plant'=1}; settings=@{}
    presentationFiles=@{ 'garden.pck'=(Garden-Hash 'garden.pck') }
    visuals=@(@{key='plant';component='flora.plant';scene=('res://' + $Mechanic + '/' + $Version + '/flora.tscn');layer=55;
        bindings=@(@{property='scale';component='flora.plant';field='biomass';scale=0.65;offset=0.35})})
}
$gardenManifest | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $gardenStage 'module.json') -Encoding UTF8
[IO.Directory]::Move($gardenStage, $gardenDestination)
Write-Output ('Garden package prepared: ' + $gardenDestination)

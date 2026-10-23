[CmdletBinding()]
param([switch]$NoBuild, [switch]$Headless)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$runtimeDefinitions = @(
    @{ Id='movement'; Project='Movement'; Type='RuntimeMovement'; Schemas=@{'movement.heading'=1; 'movement.control'=1}; Provides=@('movement'); Renderer='AgentPresentation'; Order=100 },
    @{ Id='vision'; Project='Vision'; Type='RuntimeVision'; Schemas=@{'vision.appearance'=1}; Provides=@('vision.appearance') },
    @{ Id='temperature'; Project='Temperature'; Type='RuntimeTemperature'; Schemas=@{'temperature.body'=1; 'temperature.emitter'=1}; Requires=@('vision.appearance'); Renderer='TemperaturePresentation'; Settings='temperature' },
    @{ Id='homeostasis'; Project='Homeostasis'; Type='RuntimeHomeostasis'; Schemas=@{}; Provides=@('motivation'); Settings='reward' },
    @{ Id='hunger'; Project='Hunger'; Type='RuntimeHunger'; Schemas=@{'hunger.body'=1; 'nutrition'=1}; Provides=@('nutrition'); Settings='hunger' },
    @{ Id='food'; Project='Food'; Type='RuntimeFood'; Schemas=@{'food.apple'=1}; Requires=@('nutrition','vision.appearance'); Dependencies=@(@{id='hunger';version='1.0.0'},@{id='vision';version='1.0.0'}); Renderer='FoodPresentation'; Order=50; Settings='foodSpawner' }
)
$legacyDefaults = Get-Content -LiteralPath (Join-Path $RepoRoot 'configs/default.json') -Raw | ConvertFrom-Json
if (!$NoBuild) { Invoke-Dotnet build src/ArtificialLife.Presentation.Godot -c Release --verbosity quiet }
foreach ($definition in $runtimeDefinitions) {
    $projectName = 'ArtificialLife.Modules.' + $definition.Project
    if (!$NoBuild) { Invoke-Dotnet build ('src/' + $projectName) -c Release --verbosity quiet }
    $destination = Join-Path $RepoRoot ('artifacts/packages/' + $definition.Id + '/1.0.0')
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    $sourceDirectory = Join-Path $RepoRoot ('src/' + $projectName + '/bin/Release/net8.0')
    Copy-Item -LiteralPath (Join-Path $sourceDirectory ($projectName + '.dll')) -Destination $destination -Force
    Copy-Item -LiteralPath (Join-Path $sourceDirectory ($projectName + '.deps.json')) -Destination $destination -Force
    $manifest = [ordered]@{id=$definition.Id; version='1.0.0'; apiVersion=1; assembly=($projectName + '.dll'); entryType=($projectName + '.' + $definition.Type); logicHash=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $destination ($projectName + '.dll'))).Hash; schemas=$definition.Schemas; settings=@{}}
    $manifest.logicFiles = @{ ($projectName + '.deps.json') = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $destination ($projectName + '.deps.json'))).Hash }
    foreach ($field in @('Provides','Requires','Dependencies')) { if ($definition.ContainsKey($field)) { $manifest[$field] = $definition[$field] } }
    if ($definition.ContainsKey('Settings')) { $manifest.settings = $legacyDefaults.($definition.Settings) }
    if ($definition.ContainsKey('Renderer')) {
        $rendererFile = 'ArtificialLife.Presentation.Godot.dll'
        Copy-Item -LiteralPath (Join-Path $RepoRoot ('src/ArtificialLife.Presentation.Godot/.godot/mono/temp/bin/Release/' + $rendererFile)) -Destination $destination -Force
        $manifest.presentationAssembly = $rendererFile
        $manifest.presentationType = 'ArtificialLife.Presentation.Godot.' + $definition.Renderer
        $manifest.presentationHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $destination $rendererFile)).Hash
        if ($definition.ContainsKey('Order')) { $manifest.presentationOrder = $definition.Order }
    }
    $temporaryManifest = Join-Path $destination 'module.json.tmp'
    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporaryManifest -Encoding UTF8
    Move-Item -LiteralPath $temporaryManifest -Destination (Join-Path $destination 'module.json') -Force
}

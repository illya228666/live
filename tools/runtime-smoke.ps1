[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
& (Join-Path $PSScriptRoot 'package.ps1')
Invoke-Dotnet build src/ArtificialLife.Godot -c Debug --verbosity quiet
$smokeDirectory = Join-Path $RepoRoot ('artifacts/runtime-smoke-' + [Guid]::NewGuid().ToString('N'))
$smokePackages = Join-Path $smokeDirectory 'packages'
New-Item -ItemType Directory -Force -Path $smokePackages | Out-Null
foreach ($smokeId in @('movement','vision','temperature','homeostasis','hunger','food')) {
    Copy-Item -LiteralPath (Join-Path $RepoRoot ('artifacts/packages/' + $smokeId)) -Destination $smokePackages -Recurse -Force
}
foreach ($smokeId in @('temperature','movement')) {
    $smokeSource = Join-Path $smokePackages ($smokeId + '/1.0.0')
    $smokeTarget = Join-Path $smokePackages ($smokeId + '/2.0.0')
    Copy-Item -LiteralPath $smokeSource -Destination $smokeTarget -Recurse
    $smokeManifest = Get-Content -LiteralPath (Join-Path $smokeTarget 'module.json') -Raw | ConvertFrom-Json
    $smokeManifest.version = '2.0.0'
    if ($smokeId -eq 'temperature') { $smokeManifest.settings.fireStrength += 1 }
    $smokeManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $smokeTarget 'module.json') -Encoding UTF8
}
$smokeConfig = @{packageRoot=$smokePackages; inbox=(Join-Path $smokeDirectory 'inbox'); packages=@(
    @{id='movement';version='1.0.0'},@{id='vision';version='1.0.0'},@{id='temperature';version='1.0.0'},@{id='homeostasis';version='1.0.0'})}
$smokeConfigPath = Join-Path $smokeDirectory 'config.json'
$smokeConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $smokeConfigPath -Encoding UTF8
$smokeSchedule = @(
    @{atAge=1200; changesLearning=$true; request=@{id='add-hunger';packages=@(@{id='hunger';version='1.0.0'},@{id='food';version='1.0.0'})}},
    @{atAge=1600; changesLearning=$true; request=@{id='update-physics';packages=@(@{id='temperature';version='2.0.0'})}},
    @{atAge=2000; changesLearning=$false; request=@{id='update-presentation';packages=@(@{id='movement';version='2.0.0'})}},
    @{atAge=2400; changesLearning=$false; request=@{id='update-presentation-again';packages=@(@{id='movement';version='1.0.0'})}}
)
$smokeSchedulePath = Join-Path $smokeDirectory 'schedule.json'
$smokeSchedule | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $smokeSchedulePath -Encoding UTF8
$smokeGodot = Get-Godot
& $smokeGodot --headless --audio-driver Dummy --path (Join-Path $RepoRoot 'src/ArtificialLife.Godot') -- --train --config $smokeConfigPath --training-smoke-steps 3000 --activation-schedule $smokeSchedulePath --checkpoint (Join-Path $smokeDirectory 'policy')
if ($LASTEXITCODE -ne 0) { throw 'Runtime Godot integration smoke failed.' }

[CmdletBinding()]
param([switch]$Headless)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$wildlifeSmoke = Join-Path $RepoRoot ('artifacts/wildlife-smoke-' + [Guid]::NewGuid().ToString('N'))
$wildlifePackages = Join-Path $wildlifeSmoke 'packages'
New-Item -ItemType Directory -Force -Path $wildlifePackages | Out-Null
foreach ($wildlifeId in @('movement','vision','temperature','homeostasis','hunger','food')) {
    Copy-Item -LiteralPath (Join-Path $RepoRoot ('artifacts/packages/' + $wildlifeId)) -Destination $wildlifePackages -Recurse
}
# Builds only this new package. Existing development packages and the live inbox are untouched.
& (Join-Path $PSScriptRoot 'package-wildlife.ps1') -OutputRoot $wildlifePackages
$wildlifeOriginal = Join-Path $wildlifePackages 'wildlife/1.0.0'
$wildlifeReplacement = Join-Path $wildlifePackages 'wildlife/1.0.1'
Copy-Item -LiteralPath $wildlifeOriginal -Destination $wildlifeReplacement -Recurse
$wildlifeNextManifest = Get-Content -LiteralPath (Join-Path $wildlifeReplacement 'module.json') -Raw | ConvertFrom-Json
$wildlifeNextManifest.version = '1.0.1'
$wildlifeNextManifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $wildlifeReplacement 'module.json') -Encoding UTF8
$wildlifeConfig = Get-Content -LiteralPath (Join-Path $RepoRoot 'configs/runtime.json') -Raw | ConvertFrom-Json
# This scenario specifically tests a later installation, unlike the normal startup set.
$wildlifeConfig.packages = @($wildlifeConfig.packages | Where-Object { $_.id -ne 'wildlife' })
$wildlifeConfig.packageRoot = $wildlifePackages
$wildlifeConfig.inbox = Join-Path $wildlifeSmoke 'isolated-inbox'
$wildlifeConfigPath = Join-Path $wildlifeSmoke 'config.json'
$wildlifeConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $wildlifeConfigPath -Encoding UTF8
$wildlifeSchedule = @(
    @{ atAge=1200; changesLearning=$true; request=@{id='add-wildlife'; packages=@(@{id='wildlife'; version='1.0.0'})} },
    @{ atAge=1600; changesLearning=$false; request=@{id='replace-wildlife-presentation'; packages=@(@{id='wildlife'; version='1.0.1'})} }
)
$wildlifeSchedulePath = Join-Path $wildlifeSmoke 'schedule.json'
$wildlifeSchedule | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $wildlifeSchedulePath -Encoding UTF8
Invoke-Dotnet build src/ArtificialLife.Godot -c Debug --verbosity quiet
$wildlifeGodot = Get-Godot
$wildlifeArguments = @('--audio-driver','Dummy','--path',('"' + (Join-Path $RepoRoot 'src/ArtificialLife.Godot') + '"'),'--','--train',
    '--config',('"' + $wildlifeConfigPath + '"'),'--training-smoke-steps','3000','--activation-schedule',('"' + $wildlifeSchedulePath + '"'),
    '--checkpoint',('"' + (Join-Path $wildlifeSmoke 'policy') + '"'))
if ($Headless) { $wildlifeArguments = @('--headless') + $wildlifeArguments }
else { $wildlifeArguments += @('--capture',('"' + (Join-Path $wildlifeSmoke 'wildlife.png') + '"')) }
$wildlifeStdout = Join-Path $wildlifeSmoke 'stdout.log'
$wildlifeStderr = Join-Path $wildlifeSmoke 'stderr.log'
$wildlifeProcess = Start-Process -FilePath $wildlifeGodot -ArgumentList $wildlifeArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput $wildlifeStdout -RedirectStandardError $wildlifeStderr
$wildlifeProcess.WaitForExit()
$wildlifeOutput = Get-Content -LiteralPath $wildlifeStdout -Raw
Write-Output $wildlifeOutput
if ($wildlifeProcess.ExitCode -ne 0) { Get-Content -LiteralPath $wildlifeStderr; throw "Wildlife smoke failed with exit code $($wildlifeProcess.ExitCode)." }
if ($wildlifeOutput -notmatch 'ARTIFICIALLIFE_UNLOAD_SMOKE_OK' -or $wildlifeOutput -notmatch 'id=add-wildlife' -or $wildlifeOutput -notmatch 'id=replace-wildlife-presentation') {
    throw 'Wildlife activation/unload checks did not finish.'
}
if (!$Headless -and !(Test-Path -LiteralPath (Join-Path $wildlifeSmoke 'wildlife.png'))) { throw 'Wildlife capture is missing.' }
$wildlifeErrors = Get-Content -LiteralPath $wildlifeStderr -Raw
if ($wildlifeErrors -match 'Presentation disabled|ERROR:') { Write-Output $wildlifeErrors; throw 'Godot reported a rendering/runtime error.' }
Write-Output "Isolated wildlife smoke passed: $wildlifeSmoke"

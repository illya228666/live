[CmdletBinding()]
param(
    [string]$Checkpoint = '',
    [switch]$Train,
    [string]$Config = 'configs/default.json',
    [string]$VisualConfig = 'configs/live-training.json',
    [ValidateSet('1x', '10x', '100x', 'Max')][string]$TrainingSpeed,
    [switch]$Smoke,
    [ValidateRange(1, [int]::MaxValue)][int]$SmokeSteps = 1000
)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$godot = Get-Godot
$project = Join-Path $RepoRoot 'src/ArtificialLife.Godot'
if ([string]::IsNullOrWhiteSpace($Checkpoint)) {
    $Checkpoint = if ($Train) { 'artifacts/checkpoints/live-thermal' } else { 'artifacts/checkpoints/thermal' }
}
$checkpointPath = if ([IO.Path]::IsPathRooted($Checkpoint)) { $Checkpoint } else { Join-Path $RepoRoot $Checkpoint }
if (!$Train -and !(Test-Path -LiteralPath (Join-Path $checkpointPath 'manifest.json'))) { throw 'No checkpoint found. Run ./tools/train.ps1 first.' }
Invoke-Dotnet build (Join-Path $project 'ArtificialLife.Godot.csproj') -c Debug
$userArgs = @('--checkpoint', $checkpointPath)
if ($Train) {
    $configPath = if ([IO.Path]::IsPathRooted($Config)) { $Config } else { Join-Path $RepoRoot $Config }
    $visualPath = if ([IO.Path]::IsPathRooted($VisualConfig)) { $VisualConfig } else { Join-Path $RepoRoot $VisualConfig }
    $userArgs += @('--train', '--config', $configPath, '--visual-config', $visualPath)
    if ($TrainingSpeed) { $userArgs += @('--training-speed', $TrainingSpeed) }
}
if ($Smoke) {
    $smokeOption = if ($Train) { '--training-smoke-steps' } else { '--smoke-steps' }
    $userArgs += @($smokeOption, "$SmokeSteps")
    & $godot --headless --audio-driver Dummy --path $project -- @userArgs
}
else {
    & $godot --audio-driver Dummy --path $project -- @userArgs
}
if ($LASTEXITCODE -ne 0) { throw "Godot failed with exit code $LASTEXITCODE" }

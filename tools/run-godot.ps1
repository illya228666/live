[CmdletBinding()]
param([string]$Checkpoint = 'artifacts/checkpoints/thermal', [switch]$Smoke, [int]$SmokeSteps = 1000)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$godot = Get-Godot
$project = Join-Path $RepoRoot 'src/ArtificialLife.Godot'
$checkpointPath = if ([IO.Path]::IsPathRooted($Checkpoint)) { $Checkpoint } else { Join-Path $RepoRoot $Checkpoint }
if (!(Test-Path -LiteralPath (Join-Path $checkpointPath 'manifest.json'))) { throw 'No checkpoint found. Run ./tools/train.ps1 first.' }
Invoke-Dotnet build (Join-Path $project 'ArtificialLife.Godot.csproj') -c Debug
if ($Smoke) {
    & $godot --headless --audio-driver Dummy --path $project -- --checkpoint $checkpointPath --smoke-steps $SmokeSteps
}
else {
    & $godot --audio-driver Dummy --path $project -- --checkpoint $checkpointPath
}
if ($LASTEXITCODE -ne 0) { throw "Godot failed with exit code $LASTEXITCODE" }

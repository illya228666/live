param([string]$Source = 'src/ArtificialLife.Godot/visual-foundation', [Parameter(Mandatory=$true)][string]$Output, [string]$Prefix = 'live-foundation/1')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$godot = Get-Godot
& $godot --headless --audio-driver Dummy --path (Join-Path $RepoRoot 'src/ArtificialLife.Godot') -- --pack-source ([IO.Path]::GetFullPath($Source)) --pack-output ([IO.Path]::GetFullPath($Output)) --pack-prefix $Prefix
if ($LASTEXITCODE -ne 0) { throw 'Visual resource packaging failed.' }

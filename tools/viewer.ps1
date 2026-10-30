param([string]$Session='', [string]$Endpoint='', [string]$Capture='', [switch]$Smoke)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$godot = Get-Godot
$userArguments = @()
if ($Session) { $userArguments += @('--session',$Session) }
if ($Endpoint) { $userArguments += @('--endpoint',$Endpoint) }
if ($Capture) { $userArguments += @('--capture',[IO.Path]::GetFullPath($Capture)) }
if ($Smoke) { $userArguments += @('--smoke-frames','10') }
& $godot --audio-driver Dummy --path (Join-Path $RepoRoot 'src/ArtificialLife.Godot') -- @userArguments
if ($LASTEXITCODE -ne 0) { throw "Viewer failed: $LASTEXITCODE" }

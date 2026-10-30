param([string]$Checkpoint='', [switch]$Train, [string]$Config='configs/runtime.json', [string]$TrainingSpeed='Max', [switch]$Smoke)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$worldDirectory = Join-Path $RepoRoot ('artifacts/worlds/interactive-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $worldDirectory -Force | Out-Null
$cli = Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
$arguments = @('world','run','--directory',$worldDirectory,'--speed',$TrainingSpeed)
if ($Train) { $arguments += @('--mode','learning','--config',[IO.Path]::GetFullPath($Config)); if($Checkpoint){$arguments+=@('--checkpoint',[IO.Path]::GetFullPath($Checkpoint))} }
else { if (!$Checkpoint) { $Checkpoint='artifacts/checkpoints/thermal' }; $arguments += @('--mode','inference','--policy',[IO.Path]::GetFullPath($Checkpoint)) }
$start = [Diagnostics.ProcessStartInfo]::new($cli)
$start.WorkingDirectory=$RepoRoot; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
$process = [Diagnostics.Process]::Start($start)
$watch=[Diagnostics.Stopwatch]::StartNew()
$identity=Join-Path $worldDirectory 'session-id'
while (!(Test-Path $identity)) { if($process.HasExited -or $watch.Elapsed.TotalSeconds -gt 30){throw 'World startup failed.'}; Start-Sleep -Milliseconds 100 }
$session=(Get-Content $identity -Raw).Trim()
try { & (Join-Path $PSScriptRoot 'viewer.ps1') -Session $session -Smoke:$Smoke }
finally { if($Smoke -and !$process.HasExited) { & $cli world stop --directory $worldDirectory; if(!$process.WaitForExit(15000)){$process.Kill($true)} } }

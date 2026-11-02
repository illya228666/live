[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Checkpoint,
      [Parameter(Mandatory=$true)][string]$Output, [string[]]$Sources=@(), [string]$Cli='')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if (Test-Path -LiteralPath $Output) { throw 'Use a new verification output directory.' }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$evolutionSources = Join-Path ([IO.Path]::GetFullPath($Output)) 'sources.json'
$evolutionGeneration = [IO.Path]::GetFullPath($Checkpoint)
if (Test-Path -LiteralPath (Join-Path $Checkpoint 'current')) {
    $evolutionPointer = Get-Content (Join-Path $Checkpoint 'current') -Raw | ConvertFrom-Json
    $evolutionGeneration = Join-Path $evolutionGeneration $evolutionPointer.Generation
}
$evolutionSave = Get-Content (Join-Path $evolutionGeneration 'world.json') -Raw | ConvertFrom-Json
$evolutionInputs = @($evolutionSave.Modules | ForEach-Object { Join-Path $evolutionGeneration ('packages/' + $_.Id + '/' + $_.Version) })
if ($Sources.Count) { $evolutionInputs=@($Sources | ForEach-Object { [IO.Path]::GetFullPath($_) }) }
ConvertTo-Json -InputObject $evolutionInputs | Set-Content -LiteralPath $evolutionSources -Encoding UTF8
& (Get-LiveCli $Cli) world verify --checkpoint $evolutionGeneration --sources-file $evolutionSources --output ([IO.Path]::GetFullPath($Output))
if ($LASTEXITCODE -ne 0) { throw ('Checkpoint continuation failed; see ' + (Join-Path $Output 'error.txt')) }
Write-Output ('Verified real checkpoint: 1000 headless steps, exact restore, identical next step. ' + $Output)

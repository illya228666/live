[CmdletBinding()]
param([switch]$NoBuild)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if (!$NoBuild) { Invoke-Dotnet build src/ArtificialLife.Modules.SignalDiagnostic -c Release --verbosity quiet }
$signalName='ArtificialLife.Modules.SignalDiagnostic'
$signalSource=Join-Path $RepoRoot ('src/' + $signalName + '/bin/Release/net10.0')
$signalDefinitions=@(
    @{Version='1.0.0';Mode='base';Noise=0;Need=$true},
    @{Version='1.1.0';Mode='base';Noise=0;Need=$false},
    @{Version='2.0.0';Mode='identity';Noise=0;Need=$true},
    @{Version='3.0.0';Mode='channel';Noise=0;Need=$true},
    @{Version='4.0.0';Mode='action';Noise=0;Need=$true},
    @{Version='5.0.0';Mode='distractors';Noise=8;Need=$true},
    @{Version='6.0.0';Mode='distractors';Noise=32;Need=$true}
)
foreach ($signalDefinition in $signalDefinitions) {
    $signalDirectory=Join-Path $RepoRoot ('artifacts/packages/signal-diagnostic/' + $signalDefinition.Version)
    $signalDll=$signalName + '.dll';$signalDeps=$signalName + '.deps.json'
    $signalHash=(Get-FileHash -LiteralPath (Join-Path $signalSource $signalDll) -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath (Join-Path $signalDirectory 'module.json')) {
        $signalExisting=Get-Content -LiteralPath (Join-Path $signalDirectory 'module.json') -Raw|ConvertFrom-Json
        if ($signalExisting.logicHash -ne $signalHash) {throw 'Published signal diagnostic changed; use a new package version.'}
        continue
    }
    New-Item -ItemType Directory -Force -Path $signalDirectory|Out-Null
    Copy-Item -LiteralPath (Join-Path $signalSource $signalDll) -Destination $signalDirectory
    Copy-Item -LiteralPath (Join-Path $signalSource $signalDeps) -Destination $signalDirectory
    @{id='signal-diagnostic';version=$signalDefinition.Version;apiVersion=1;assembly=$signalDll
      entryType=($signalName + '.RuntimeSignalDiagnostic');logicHash=$signalHash
      logicFiles=@{$signalDeps=(Get-FileHash -LiteralPath (Join-Path $signalSource $signalDeps) -Algorithm SHA256).Hash}
      schemas=@{'signal-diagnostic.body'=1};settings=@{mode=$signalDefinition.Mode;distractors=$signalDefinition.Noise;needEnabled=$signalDefinition.Need}
    }|ConvertTo-Json -Depth 15|Set-Content -LiteralPath (Join-Path $signalDirectory 'module.json') -Encoding utf8
}

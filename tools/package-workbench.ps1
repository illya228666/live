[CmdletBinding()]
param([switch]$NoBuild)
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
if (!$NoBuild) { Invoke-Dotnet build src/ArtificialLife.Modules.Workbench -c Release --verbosity quiet }
$workbenchProject = 'ArtificialLife.Modules.Workbench'
$workbenchSource = Join-Path $RepoRoot ('src/' + $workbenchProject + '/bin/Release/net10.0')
foreach ($workbenchVersion in @('1.0.0','2.0.0','3.0.0')) {
    $workbenchDirectory = Join-Path $RepoRoot ('artifacts/packages/workbench/' + $workbenchVersion)
    $workbenchDll = $workbenchProject + '.dll'
    $workbenchDeps = $workbenchProject + '.deps.json'
    $workbenchHash = (Get-FileHash -LiteralPath (Join-Path $workbenchSource $workbenchDll) -Algorithm SHA256).Hash
    $workbenchManifest = [ordered]@{
        id='workbench';version=$workbenchVersion;apiVersion=1;assembly=$workbenchDll
        entryType=($workbenchProject + '.RuntimeWorkbench');logicHash=$workbenchHash
        logicFiles=@{$workbenchDeps=(Get-FileHash -LiteralPath (Join-Path $workbenchSource $workbenchDeps) -Algorithm SHA256).Hash}
        schemas=@{'workbench.device'=1};requires=@('vision.appearance');dependencies=@(@{id='vision';version='1.0.0'})
        settings=@{needEnabled=($workbenchVersion -ne '1.0.0');noiseEnabled=($workbenchVersion -eq '3.0.0')}
    }
    if (Test-Path -LiteralPath (Join-Path $workbenchDirectory 'module.json')) {
        $workbenchPrevious = Get-Content -LiteralPath (Join-Path $workbenchDirectory 'module.json') -Raw | ConvertFrom-Json
        if ($workbenchPrevious.logicHash -ne $workbenchHash) { throw 'Published workbench changed. Publish a new package version.' }
        continue
    }
    New-Item -ItemType Directory -Force -Path $workbenchDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $workbenchSource $workbenchDll) -Destination $workbenchDirectory
    Copy-Item -LiteralPath (Join-Path $workbenchSource $workbenchDeps) -Destination $workbenchDirectory
    $workbenchManifest | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $workbenchDirectory 'module.json') -Encoding utf8
}

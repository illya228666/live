[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Description, [string]$Id='', [string]$Version='',
      [string]$OutputRoot='artifacts/packages', [switch]$NoBuild, [string]$Cli='')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$descriptionPath=[IO.Path]::GetFullPath($Description)
$sourceRoot=Split-Path $descriptionPath
$descriptionData=Get-Content -LiteralPath $descriptionPath -Raw | ConvertFrom-Json -AsHashtable
$project=[IO.Path]::GetFullPath((Join-Path $sourceRoot $descriptionData.project))
$packages=@($descriptionData.packages | Where-Object { !$Id -or $_.manifest.id -eq $Id })
if (!$packages.Count) { throw 'No matching package in description.' }
foreach ($definition in $packages) {
    $packageVersion=if ($Version) { $Version } else { $definition.manifest.version }
    $existing=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ($definition.manifest.id+'/'+$packageVersion)
    if (Test-Path -LiteralPath $existing) { throw 'Published versions are immutable. Check existing artifacts with evolve -Action check -Sources, or choose a new version.' }
}
if (!$NoBuild) { Invoke-Dotnet build $project -c Release --verbosity quiet }
$target=& $script:Dotnet msbuild $project -p:Configuration=Release -getProperty:TargetPath
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $target)) { throw 'Build the described project, or remove -NoBuild.' }
$binaryRoot=Split-Path $target
$cliPath=Get-LiveCli $Cli
$published=@()
foreach ($definition in $packages) {
    $manifest=$definition.manifest
    if ($Version) { $manifest.version=$Version }
    foreach ($value in @($manifest.id,$manifest.version)) {
        if ($value -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $value -in @('.','..')) { throw 'Invalid package identity.' }
    }
    $stage=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('.staging-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    # The description reuses ModuleManifest fields; only generated hashes/files are added here.
    $manifest.assembly=Split-Path $target -Leaf
    $manifest.logicFiles=@{}
    foreach ($file in Get-ChildItem -LiteralPath $binaryRoot -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -like '*.deps.json' }) {
        Copy-Item -LiteralPath $file.FullName -Destination $stage
        $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        if ($file.Name -eq $manifest.assembly) { $manifest.logicHash=$hash } else { $manifest.logicFiles[$file.Name]=$hash }
    }
    $manifest.presentationFiles=@{}
    if ($definition.ContainsKey('files')) {
        foreach ($entry in $definition.files.GetEnumerator()) {
            $destination=[IO.Path]::GetFullPath((Join-Path $stage $entry.Key))
            if (!$destination.StartsWith($stage+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Asset path escapes package.' }
            New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $sourceRoot $entry.Value) -Destination $destination
            $manifest.presentationFiles[$entry.Key]=(Get-FileHash -LiteralPath $destination).Hash
        }
    }
    if ($definition.ContainsKey('visualPack')) {
        $pack=$definition.visualPack
        if ($pack.file -ne [IO.Path]::GetFileName($pack.file)) { throw 'PCK file must be a simple file name.' }
        & (Join-Path $PSScriptRoot 'pack-visuals.ps1') -Source (Join-Path $sourceRoot $pack.source) -Output (Join-Path $stage $pack.file) -Prefix ($manifest.id+'/'+$manifest.version) | Out-Null
        $manifest.presentationFiles[$pack.file]=(Get-FileHash -LiteralPath (Join-Path $stage $pack.file)).Hash
    }
    if ($manifest.ContainsKey('presentationAssembly')) {
        $manifest.presentationHash=(Get-FileHash -LiteralPath (Join-Path $stage $manifest.presentationAssembly)).Hash
    }
    $json=($manifest | ConvertTo-Json -Depth 30).Replace('@id@',$manifest.id).Replace('@version@',$manifest.version)
    $json | Set-Content -LiteralPath (Join-Path $stage 'module.json') -Encoding utf8
    & $cliPath package publish --sources $stage --root ([IO.Path]::GetFullPath($OutputRoot)) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw ('Package validation/publication failed; staging retained: '+$stage) }
    $published+=Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ($manifest.id+'/'+$manifest.version)
    # Delete only our exact generated staging path, never a published version.
    Remove-Item -LiteralPath $stage -Recurse
}
[ordered]@{status='passed';packages=$published} | ConvertTo-Json -Compress

[CmdletBinding()]
param([string[]]$Package=@(), [string[]]$Sources=@(), [string]$Session='', [string]$Directory='', [string]$PackageRoot='artifacts/packages')
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$cli=Join-Path $RepoRoot 'src/ArtificialLife.Cli/bin/Release/net10.0/ArtificialLife.Cli.exe'
$catalog=if([IO.Path]::IsPathRooted($PackageRoot)){$PackageRoot}else{Join-Path $RepoRoot $PackageRoot}
$inputs=@($Sources | ForEach-Object {[IO.Path]::GetFullPath($_)})
foreach($reference in $Package) {
    $parts=$reference.Split('@')
    if($parts.Length -ne 2 -or $parts[0] -notmatch '^[a-zA-Z0-9_.-]+$' -or $parts[1] -notmatch '^[a-zA-Z0-9_.-]+$' -or $parts[0] -in @('.','..') -or $parts[1] -in @('.','..')){throw 'Use exact id@version package references.'}
    $inputs+=Join-Path $catalog ($parts[0]+'/'+$parts[1])
}
if($inputs.Count -eq 0){throw 'Specify -Package id@version or -Sources package folders, manifests or ZIPs.'}
if($Directory) {
    & $cli world update --directory ([IO.Path]::GetFullPath($Directory)) --sources ($inputs -join ';')
} else {
    if(!$Session) {
        $sessions=& $cli world list | ConvertFrom-Json
        $eligible=@($sessions | Where-Object {$_.Mode -in @('Learning','WorldOnly','Inference') -and !$_.ParentId})
        if($eligible.Count -ne 1){throw 'Specify -Session or -Directory to choose one active world.'}
        $Session=$eligible[0].SessionId
    }
    & $cli world update --session $Session --value ($inputs -join ';')
}
if($LASTEXITCODE -ne 0){throw 'Package update request was rejected.'}

[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
Initialize-Dotnet
$target = Join-Path $RepoRoot 'artifacts/packages/wildlife/1.1.0/module.json'
if (!(Test-Path -LiteralPath $target)) {
    & (Join-Path $PSScriptRoot 'package-wildlife.ps1') -Version '1.1.0'
} else {
    $manifest = Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
    $binary = Join-Path $RepoRoot 'src/ArtificialLife.Modules.Wildlife/bin/Release/net10.0/ArtificialLife.Modules.Wildlife.dll'
    if ($manifest.logicHash -ne (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash) {
        throw 'wildlife@1.1.0 differs from the built source. Preserve the published package and use a new version for changed mechanics.'
    }
}

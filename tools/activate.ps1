[CmdletBinding()]
param([Parameter(Mandatory=$true)][string[]]$Package, [string]$Inbox='artifacts/activation')
. (Join-Path $PSScriptRoot 'common.ps1')
$activationPackages = @()
foreach ($activationPackage in $Package) {
    $activationParts = $activationPackage.Split('@')
    if ($activationParts.Length -ne 2 -or [string]::IsNullOrWhiteSpace($activationParts[0]) -or [string]::IsNullOrWhiteSpace($activationParts[1])) { throw 'Use exact id@version package references.' }
    $activationPackages += @{id=$activationParts[0];version=$activationParts[1]}
}
$activationId = [Guid]::NewGuid().ToString('N')
$activationDirectory = if ([IO.Path]::IsPathRooted($Inbox)) { $Inbox } else { Join-Path $RepoRoot $Inbox }
New-Item -ItemType Directory -Force -Path $activationDirectory | Out-Null
$activationTemporary = Join-Path $activationDirectory ($activationId + '.tmp')
@{id=$activationId;packages=$activationPackages} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $activationTemporary -Encoding UTF8
Move-Item -LiteralPath $activationTemporary -Destination (Join-Path $activationDirectory ($activationId + '.ready.json'))
Write-Output ('Submitted activation ' + $activationId + '; result: ' + (Join-Path $activationDirectory ($activationId + '.result.json')))

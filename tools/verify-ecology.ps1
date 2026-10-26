#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$First, [Parameter(Mandatory)][string]$Second, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$a = Get-Content -LiteralPath $First -Raw | ConvertFrom-Json -AsHashtable
$b = Get-Content -LiteralPath $Second -Raw | ConvertFrom-Json -AsHashtable
if (!$a.Passed -or !$b.Passed) { throw 'Both complete audits must pass before reproducibility admission.' }
$firstProvenance = Join-Path (Split-Path -Parent $First) 'provenance.json'
$secondProvenance = Join-Path (Split-Path -Parent $Second) 'provenance.json'
if (!(Test-Path -LiteralPath $firstProvenance) -or !(Test-Path -LiteralPath $secondProvenance)) { throw 'Both audits require executable provenance.' }
$firstExecution = (Get-Content -LiteralPath $firstProvenance -Raw | ConvertFrom-Json).ExecutionHash
$secondExecution = (Get-Content -LiteralPath $secondProvenance -Raw | ConvertFrom-Json).ExecutionHash
if (!$firstExecution -or $firstExecution -cne $secondExecution) { throw 'Audits used different executable assemblies/platforms.' }
if ($a.Controls.Count -ne $a.Protocol.Seeds.Count * $a.Protocol.Policies.Count -or $b.Controls.Count -ne $a.Controls.Count) {
    throw 'Unexpected control count (this verifier expects one condition per report).'
}
$script:largestError = 0.0
function Compare-Node($Left, $Right, [string]$At) {
    if ($null -eq $Left -or $null -eq $Right) {
        if ($null -ne $Left -or $null -ne $Right) { throw "Availability differs at $At" }
    } elseif ($Left -is [System.Collections.IDictionary]) {
        if ($Right -isnot [System.Collections.IDictionary] -or $Left.Count -ne $Right.Count) { throw "Keys differ at $At" }
        foreach ($key in $Left.Keys) {
            if (!$Right.Contains($key)) { throw "Missing $At/$key" }
            Compare-Node $Left[$key] $Right[$key] "$At/$key"
        }
    } elseif ($Left -is [array]) {
        if ($Right -isnot [array] -or $Left.Count -ne $Right.Count) { throw "Lengths differ at $At" }
        for ($i=0; $i -lt $Left.Count; $i++) { Compare-Node $Left[$i] $Right[$i] "$At/$i" }
    } elseif ($Left -is [double] -or $Left -is [long] -or $Left -is [int] -or $Left -is [decimal]) {
        $difference = [math]::Abs([double]$Left - [double]$Right)
        if (![double]::IsFinite($difference) -or $difference -gt 1e-9) { throw "Numeric mismatch at $At" }
        $script:largestError = [math]::Max($script:largestError, $difference)
    } elseif ($Left -cne $Right) { throw "Mismatch at $At" }
}
# Full reports contain no timing, output paths or artifact identifiers. Includes action/state
# hashes, all RNG-bearing world trajectories, per-seed metrics, blocks and protocol thresholds.
Compare-Node $a $b 'report'
$verification = [ordered]@{
    schemaVersion=1;passed=$true;controls=$a.Controls.Count;stepsPerControl=$a.Controls[0].Steps
    fingerprint=$a.Fingerprint;numericTolerance=1e-9;maximumNumericDifference=$script:largestError
    actionHashesMatch=$true;physicalStateHashesMatch=$true
    executionHash=$firstExecution;executableAssembliesMatch=$true
    firstReportSha256=(Get-FileHash -LiteralPath $First -Algorithm SHA256).Hash
    secondReportSha256=(Get-FileHash -LiteralPath $Second -Algorithm SHA256).Hash
}
$absolute = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $absolute) { throw 'Verification output already exists; use a new file.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
$verification | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath ($absolute + '.tmp') -Encoding utf8
[IO.File]::Move($absolute + '.tmp', $absolute)
$verification | ConvertTo-Json

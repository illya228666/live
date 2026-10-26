#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string[]]$Reports, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$absolute = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $absolute) { throw 'Use a new output directory; preserve previous summaries.' }
[IO.Directory]::CreateDirectory($absolute) | Out-Null
$keys = @('temperature.mae','temperature.outside','hunger.mean','hunger.above','wildlife.distance','wildlife.proximity','wildlife.contact-proxy','wildlife.encounters','food.consumed','movement.fraction')
$csv = [Collections.Generic.List[object]]::new()
$summary = [Collections.Generic.List[object]]::new()
foreach ($path in $Reports) {
    $report = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    $scenario = Split-Path -Leaf (Split-Path -Parent $path)
    $controls = foreach ($control in $report.Controls) {
        $metrics = [ordered]@{}
        foreach ($key in $keys) { $metrics[$key] = $control.Metrics[$key].Value }
        $row = [ordered]@{scenario=$scenario;seed=$control.Seed;condition=$control.Condition;policy=$control.Policy;steps=$control.Steps}
        foreach ($key in $keys) { $row[$key] = $metrics[$key] }
        $csv.Add([pscustomobject]$row)
        $blocks = if ($control.Policy -like 'joint*') {
            @(foreach ($block in $control.Blocks) {
                $values = [ordered]@{}
                foreach ($key in $keys) { $values[$key] = $block[$key] }
                $values
            })
        } else { @() }
        [ordered]@{condition=$control.Condition;seed=$control.Seed;policy=$control.Policy;steps=$control.Steps;metrics=$metrics;blocks=$blocks;actionHash=$control.ActionHash;stateHash=$control.StateHash}
    }
    $stats = foreach ($group in $csv | Where-Object scenario -eq $scenario | Group-Object condition,policy) {
        foreach ($key in $keys) {
            $values = @($group.Group | ForEach-Object { if ($null -ne $_.$key) { [double]$_.$key } })
            $mean = if ($values.Count) { ($values | Measure-Object -Average).Average } else { $null }
            $sd = if ($values.Count -gt 1) { [math]::Sqrt((($values | ForEach-Object { [math]::Pow($_ - $mean,2) } | Measure-Object -Sum).Sum) / ($values.Count-1)) } else { $null }
            [ordered]@{condition=$group.Group[0].condition;policy=$group.Group[0].policy;metric=$key;n=$values.Count;mean=$mean;sd=$sd;minimum=($values | Measure-Object -Minimum).Minimum;maximum=($values | Measure-Object -Maximum).Maximum}
        }
    }
    # Differences are paired by world seed, never by individual step.
    $pairs = foreach ($alternative in @('joint-no-food','joint-no-safety','joint-no-thermal','patrol','random')) {
        foreach ($control in $controls | Where-Object policy -eq 'joint') {
            $other = @($controls | Where-Object { $_.condition -eq $control.condition -and $_.seed -eq $control.seed -and $_.policy -eq $alternative })
            if ($other.Count -ne 1) { continue }
            $difference = [ordered]@{}
            foreach ($key in $keys) { $difference[$key] = if ($null -eq $control.metrics[$key] -or $null -eq $other[0].metrics[$key]) { $null } else { $control.metrics[$key] - $other[0].metrics[$key] } }
            [ordered]@{condition=$control.condition;seed=$control.seed;reference='joint';alternative=$alternative;referenceMinusAlternative=$difference}
        }
    }
    $definitions = [ordered]@{}
    foreach ($key in $keys) {
        $metric = $report.Controls[0].Metrics[$key]
        $definitions[$key] = [ordered]@{unit=$metric.Unit;higherIsBetter=$metric.HigherIsBetter;description=$metric.Description;proxy=$metric.Proxy}
    }
    $value = [ordered]@{
        schemaVersion=1;passed=$report.Passed;fingerprint=$report.Fingerprint;factors=$report.Factors;protocol=$report.Protocol
        fullReport=[IO.Path]::GetRelativePath($root,[IO.Path]::GetFullPath($path));fullReportSha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        metricDefinitions=$definitions;controls=@($controls);statistics=@($stats);pairs=@($pairs);failures=$report.Failures;limitations=$report.Limitations
    }
    $value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $absolute ($scenario + '.json')) -Encoding utf8
    $summary.Add([ordered]@{scenario=$scenario;passed=$report.Passed;controls=$controls.Count;fingerprint=$report.Fingerprint;statistics=@($stats)})
}
$csv | Export-Csv -LiteralPath (Join-Path $absolute 'controls.csv') -NoTypeInformation -Encoding utf8
$summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $absolute 'summary.json') -Encoding utf8

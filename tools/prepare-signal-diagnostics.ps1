#requires -Version 7.2
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'common.ps1')
$signalCases=@(
    @{Id='identity';Version='2.0.0';Initial='1.0.0'},
    @{Id='channel';Version='3.0.0';Initial='1.0.0'},
    @{Id='action';Version='4.0.0';Initial='1.0.0'},
    @{Id='noise8';Version='5.0.0';Initial='1.0.0'},
    @{Id='noise32';Version='6.0.0';Initial='1.0.0'},
    @{Id='neutral';Version='2.0.0';Initial='1.1.0'}
)
foreach ($signalCase in $signalCases) {
    foreach ($signalAge in @('late','fresh')) {
        $signalConfig=@{
            schemaVersion=1
            runtime=@{packageRoot='../artifacts/packages';world=@{width=4;height=4;timeStep=1;episodeSteps=400;movementSpeed=1}
                packages=@(@{id='homeostasis';version='1.0.0'},@{id='signal-diagnostic';version=$(if ($signalAge -eq 'late') {$signalCase.Initial} else {$signalCase.Version})})}
            motivations=@(@{id='fixed-goal';package=@{id='homeostasis';version='1.0.0'};settings=@{improvementWeight=0;maximumDriveWeight=10}})
            learners=@(@{id='learning';controller=@{algorithm='generic-actor-critic';parameters=@{features=64;exploration='uniform'}}})
            trainingSeeds=@(1741,1753,1777);evaluationSeeds=@(30101,30203,30307);episodesPerSeed=1
            evaluationSteps=2048;evaluateEvery=5000;lifeMetricWindowSteps=1000
            behavior=@{evaluators=@(@{id='drives'},@{id='signal-diagnostic-v1'});criteria=@()}
            phases=@()
        }
        if ($signalAge -eq 'late') {
            $signalConfig.learners+=@{id='frozen';controller=@{algorithm='generic-actor-critic';parameters=@{features=64;exploration='uniform';freezeAfterSteps=20000}}}
            $signalConfig.phases+=@{id='base';steps=20000}
            $signalConfig.phases+=@{id='changed';steps=20000;activate=@(@{id='signal-diagnostic';version=$signalCase.Version})}
        } else { $signalConfig.phases+=@{id='changed';steps=20000} }
        $signalPath=Join-Path $RepoRoot ('configs/research-signal-' + $signalCase.Id + '-' + $signalAge + '.json')
        if (Test-Path -LiteralPath $signalPath) {throw ('Preserve configurations: ' + $signalPath)}
        $signalConfig|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $signalPath -Encoding utf8
    }
}

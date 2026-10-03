using ArtificialLife.Application;
using ArtificialLife.Core;
using Xunit;

namespace ArtificialLife.Tests;

public sealed class HomeostasisTests
{
    [Fact]
    public void SatisfactionIsClampedAndBalanceUsesWeightsIndependentlyOfMetadataAndReward()
    {
        DriveState[] states = [new("a", 12, 10, 10, 3), new("b", 2, 0, 1, 1), new("ignored", 99, 0, 1, 0)];
        var report = HomeostasisReport.Measure(states, [new("a", "Need A", "units")]);
        Assert.Equal(0.2, report.Drives["a"].NormalizedError, 12);
        Assert.Equal(0.8, report.Drives["a"].Satisfaction, 12);
        Assert.Equal(0, report.Drives["b"].Satisfaction);
        Assert.Equal(0.6, report.Balance, 12);
        Assert.Equal("Need A", report.Drives["a"].Name);
        Assert.Equal("b", report.Drives["b"].Name);
        Assert.Equal(report.Balance, HomeostasisReport.Measure(states).Balance);
        var metrics = new MetricAccumulator();
        metrics.Add(report, -0.9);
        Assert.Equal(0.6, metrics.Metrics().MeanBalance, 12);
        Assert.Equal(-0.9, metrics.Metrics().MeanReward);
    }

    [Fact]
    public void EmptyAndZeroWeightDrivesHaveNeutralBalance()
    {
        Assert.Equal(1, HomeostasisReport.Measure([]).Balance);
        Assert.Equal(1, HomeostasisReport.Measure([new("passive", 99, 0, 1, 0)]).Balance);
    }

    [Fact]
    public void LateDriveFlowsFromProviderIntoReportsHistoryAndRollingMetricsWithoutChangingOldSamples()
    {
        var provider = new ChangingDrives();
        var simulation = new Simulation(new(), new(), [], [], [], [provider]);
        simulation.Reset(42);
        var first = HomeostasisReport.Capture(simulation);
        var rolling = new RollingMetricAccumulator(2);
        rolling.Add(first, 0.1);
        provider.Active = true;
        var second = HomeostasisReport.Capture(simulation);
        rolling.Add(second, 0.2);
        TrainingSample[] history = [new(1, 1, 1, 0, 0, first, 0.1, false), new(2, 1, 2, 0, 0, second, 0.2, false)];
        Assert.False(history[0].Homeostasis.Drives.ContainsKey("late"));
        Assert.Equal("late", history[1].Homeostasis.Drives["late"].Name);
        Assert.Equal(1, rolling.Metrics().Drives["late"].Samples);
        Assert.Equal(0.25, rolling.Metrics().Drives["late"].MeanSatisfaction);
        provider.Active = false;
        rolling.Add(HomeostasisReport.Capture(simulation), 0.3);
        Assert.Equal(1, rolling.Metrics().Drives["late"].Samples);
        rolling.Add(first, 0.4);
        Assert.False(rolling.Metrics().Drives.ContainsKey("late"));
        Assert.Equal(2, rolling.Metrics().Drives["base"].Samples);
    }

    private sealed class ChangingDrives : IDriveProvider
    {
        public bool Active;
        public IEnumerable<DriveState> GetDrives(AgentState agent)
        {
            yield return new("base", 0, 0, 1, 1);
            if (Active) yield return new("late", 0.75, 0, 1, 1);
        }
    }
}

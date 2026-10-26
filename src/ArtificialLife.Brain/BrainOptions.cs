namespace ArtificialLife.Brain;

public sealed record NetworkOptions
{
    public int ObservationCapacity { get; init; } = 256;
    public int ActionCapacity { get; init; } = 256;
    public int FeatureWidth { get; init; } = 4;
    public int TypeEmbeddingSize { get; init; } = 8;
    public int StateSize { get; init; } = 32;
    public int HiddenSize { get; init; } = 64;

    public void Validate()
    {
        if (ObservationCapacity < 1 || ActionCapacity < 1 || FeatureWidth < 1 || TypeEmbeddingSize < 1 || StateSize < 1 || HiddenSize < 1)
        {
            throw new ArgumentException("Network dimensions must be positive.");
        }
    }
}

public sealed record LearningOptions
{
    public double LearningRate { get; init; } = 0.001;
    public double Gamma { get; init; } = 0.97;
    public double EpsilonStart { get; init; } = 1;
    public double EpsilonEnd { get; init; } = 0.05;
    public int EpsilonDecaySteps { get; init; } = 50000;
    public int ExplorationMaxDuration { get; init; } = 1;
    public int BatchSize { get; init; } = 64;
    public int ReplayCapacity { get; init; } = 50000;
    public int WarmupSteps { get; init; } = 1000;
    public int TrainEvery { get; init; } = 4;
    public int TargetSyncInterval { get; init; } = 500;
    public double GradientClip { get; init; } = 5;
    public int TrainingSteps { get; init; } = 100000;
    public int LogEvery { get; init; } = 5000;
    public int Seed { get; init; } = 42;
    public double AdaptationEpsilon { get; init; } = 0.3;
    public int AdaptationSteps { get; init; } = 10000;
    public int ObservationIntroductionSteps { get; init; } = 10000;
    public double PrioritizedReplayAlpha { get; init; }
    public double PrioritizedReplayBeta { get; init; } = 0.4;

    public void Validate()
    {
        if (!double.IsFinite(LearningRate) || LearningRate <= 0 || !double.IsFinite(Gamma) || Gamma is < 0 or > 1 ||
            EpsilonStart is < 0 or > 1 || EpsilonEnd is < 0 or > 1 || !double.IsFinite(EpsilonStart) || !double.IsFinite(EpsilonEnd) ||
            EpsilonStart < EpsilonEnd || EpsilonDecaySteps < 1 || ExplorationMaxDuration is < 1 or > 10000 || BatchSize < 1 || ReplayCapacity < BatchSize ||
            WarmupSteps < BatchSize || TrainEvery < 1 || TargetSyncInterval < 1 || TrainingSteps < 1 || LogEvery < 1 ||
            !double.IsFinite(GradientClip) || GradientClip <= 0 || !double.IsFinite(AdaptationEpsilon) ||
            AdaptationEpsilon is < 0 or > 1 || AdaptationSteps < 1 || ObservationIntroductionSteps < 1 ||
            !double.IsFinite(PrioritizedReplayAlpha) || PrioritizedReplayAlpha is < 0 or > 1 ||
            !double.IsFinite(PrioritizedReplayBeta) || PrioritizedReplayBeta is < 0 or > 1)
        {
            throw new ArgumentException("Invalid learning options.");
        }
    }
}

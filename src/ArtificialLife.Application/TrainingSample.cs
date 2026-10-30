using ArtificialLife.Brain;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

/// <summary>An actual completed transition, captured before an episode reset.</summary>
public sealed record TrainingSample(long Step, int Episode, long EpisodeStep, double X, double Y,
    HomeostasisReport Homeostasis, double Reward, bool Terminal);

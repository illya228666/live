using Xunit.v3;
using Xunit.Sdk;

// Torch RNG/thread count are process-global; neural tests must not race one another.
[assembly: Parallelization(Mode = ParallelMode.None)]

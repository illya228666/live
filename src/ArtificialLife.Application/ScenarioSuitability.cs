using System.Text.Json;
using System.Security.Cryptography;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record ScenarioDescription(string Fingerprint, string[] Factors, RuntimeOptions[] Conditions)
{
    public string ScheduleFingerprint { get; init; } = "";
}
public sealed record FactorSuitability(string Factor, string Verdict, string PassiveEvidence, string ActiveEvidence, string RepairRule);
public sealed record ScenarioReview(int SchemaVersion, string Fingerprint, bool Passed, FactorSuitability[] Factors, string Evidence)
{
    public string EvidenceSha256 { get; init; } = "";
    public string? ScheduleFingerprint { get; init; }
}

/// <summary>Suitability is about physical tasks, independent of the studied reward and learner.</summary>
public static class ScenarioSuitability
{
    public static ScenarioDescription Describe(ExperimentDefinition definition)
    {
        var job = definition.Jobs().First();
        using var runtime = RuntimeSessionFactory.Create(job.Runtime, new InspectionController());
        var conditions = new List<RuntimeOptions>();
        var factors = new HashSet<string>(StringComparer.Ordinal);
        var evidence = new List<object>();
        var motivationIds = definition.Motivations.Select(m => m.Package.Id).Append(definition.MotivationSlot).ToHashSet(StringComparer.Ordinal);
        foreach (var phase in definition.Phases)
        {
            if (phase.Activate.Length > 0)
            {
                var result = runtime.Activate(new("inspect-" + phase.Id, phase.Activate));
                if (!result.Accepted) throw new InvalidDataException(result.Message);
            }
            var options = phase.EvaluationRuntime ?? job.Runtime with
            {
                Packages = runtime.Packages.Select(m => new PackageReference(m.Id, m.Version)).ToArray(),
                Settings = runtime.Packages.ToDictionary(m => m.Id, m => m.Settings.Clone())
            };
            using var evaluation = RuntimeSessionFactory.Create(options, new InspectionController());
            foreach (string key in evaluation.Report().Drives.Keys) factors.Add(key);
            conditions.Add(options);
            evidence.Add(new
            {
                phase.Id, options.World,
                Modules = evaluation.Packages.Where(m => !motivationIds.Contains(m.Id)).Select(m => new
                {
                    m.Id, m.Version, m.Assembly, m.EntryType, m.LogicHash, m.LogicFiles, m.Settings,
                    m.Schemas, m.StateSchemaVersion, m.Dependencies, m.Provides, m.Requires
                }).ToArray()
            });
        }
        string source = Path.Combine(RuntimeDefaults.RepositoryRoot(), "src", "ArtificialLife.Application", "RuntimeSession.cs");
        string runtimeSourceHash = ExperimentJson.Hash(File.ReadAllText(source).Replace("\r\n", "\n", StringComparison.Ordinal));
        string coreHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(RuntimeWorld).Assembly.Location)));
        return new(ExperimentJson.Hash(new { Version = 1, CoreHash = coreHash, RuntimeSourceHash = runtimeSourceHash, Conditions = evidence, definition.Behavior, definition.EvaluationSteps }),
            factors.Order(StringComparer.Ordinal).ToArray(), conditions.ToArray())
        // Evaluation physics is already covered by Fingerprint; archive paths and controller
        // settings must not invalidate a physical schedule review when packages are copied.
        { ScheduleFingerprint = ExperimentJson.Hash(new { Phases = definition.Phases.Select(p => p with { EvaluationRuntime = null }), definition.EvaluationSteps, definition.LifeMetricWindowSteps }) };
    }

    public static void Validate(ScenarioDescription scenario, ScenarioReview review)
    {
        if (review.SchemaVersion != 1 || !review.Passed || review.Fingerprint != scenario.Fingerprint || string.IsNullOrWhiteSpace(review.Evidence))
            throw new InvalidDataException("Scenario suitability has not passed for these physical conditions/profile/horizon. Repair and audit the scenario before scientific comparison.");
        if (review.ScheduleFingerprint is not null && review.ScheduleFingerprint != scenario.ScheduleFingerprint)
            throw new InvalidDataException("Scenario review does not cover this expansion schedule/window/horizon.");
        foreach (string factor in scenario.Factors)
        {
            var matches = review.Factors.Where(f => f.Factor == factor).ToArray();
            if (matches.Length != 1 || matches[0].Verdict != "requires-adaptation" ||
                string.IsNullOrWhiteSpace(matches[0].PassiveEvidence) || string.IsNullOrWhiteSpace(matches[0].ActiveEvidence) || string.IsNullOrWhiteSpace(matches[0].RepairRule))
                throw new InvalidDataException($"Factor '{factor}' needs passive/active evidence and a passing adaptation check. A trivial or unresolved factor must be repaired, not just documented.");
        }
        if (review.Factors.Any(f => f.Verdict != "requires-adaptation")) throw new InvalidDataException("Scenario contains an unresolved/trivial factor.");
    }

    public static int Command(string[] args, bool validate)
    {
        var values = RuntimeCommands.Arguments(args);
        if (values.Keys.Any(k => k is not ("--config" or "--output" or "--review"))) throw new ArgumentException("Unknown suitability option.");
        var description = Describe(ExperimentDefinition.Load(values["--config"]));
        if (validate)
        {
            var review = ExperimentJson.Read<ScenarioReview>(values["--review"]);
            Validate(description, review);
            string evidence = Path.GetFullPath(review.Evidence, Path.GetDirectoryName(Path.GetFullPath(values["--review"]))!);
            if (!File.Exists(evidence) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(evidence))) != review.EvidenceSha256)
                throw new InvalidDataException("Missing/modified scenario evidence. Preserve the original check and audit the repaired scenario again.");
        }
        if (values.TryGetValue("--output", out var path)) ExperimentJson.Atomic(path, description);
        Console.WriteLine(JsonSerializer.Serialize(new { description.Fingerprint, description.Factors, Validated = validate }, ExperimentJson.Lines));
        return 0;
    }

    private sealed class InspectionController : IAgentController
    {
        public ControllerLimits Limits => new(int.MaxValue, int.MaxValue, int.MaxValue);
        public ControllerStatus Status => new(0, 0, 0, 0, 0, 0);
        public ActionCandidate Choose(ObservationToken[] state, ActionCandidate[] actions, bool learning) => actions[0];
        public void Receive(AgentTransition transition) => throw new NotSupportedException();
        public void ConditionsChanged(long revision) { }
        public void Export(string path) => throw new NotSupportedException();
        public void Import(string path) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

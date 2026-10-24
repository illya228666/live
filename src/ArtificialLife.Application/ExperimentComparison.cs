using System.Globalization;
using System.Text;

namespace ArtificialLife.Application;

public sealed record SeedValue(int Seed, int WorldSeed, double? Value);
public sealed record MetricSummary(string Protocol, string Variant, string Scenario, string Phase, string Measure, string Metric,
    string Unit, bool HigherIsBetter, bool Proxy, int N, double? Mean, double? StandardDeviation, SeedValue[] Seeds);
public sealed record PairedDifference(string Protocol, string Left, string Right, string Scenario, string Phase, string Measure, string Metric,
    int N, double? MeanRightMinusLeft, double? StandardDeviation);
public sealed record ReproductionCheck(string Variant, int Seed, bool Matches, string? Difference);
public sealed record TrialSummary(string Protocol, string Variant, int Seed, int WorldSeed, string BehaviorOutcome,
    long TrainingSteps, long Updates, double TrainingSeconds, double EvaluationSeconds, double FinalEvaluationReward);
public sealed record ComparisonReport(int SchemaVersion, string[] Warnings, MetricSummary[] Metrics, PairedDifference[] Pairs,
    ReproductionCheck[] Reproductions, TrialSummary[] Trials);

public static class ExperimentComparison
{
    public static int Run(string[] args)
    {
        var arguments = RuntimeCommands.Arguments(args);
        if (arguments.Keys.Any(k => k is not ("--inputs" or "--output"))) throw new ArgumentException("Unknown comparison option.");
        string[] inputs = arguments["--inputs"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray();
        string output = Path.GetFullPath(arguments.GetValueOrDefault("--output", "artifacts/experiments/comparison"));
        var warnings = new List<string>();
        var results = new List<ExperimentRunResult>();
        foreach (string input in inputs)
        {
            string suitePath = Path.Combine(input, "suite.json");
            string[] paths;
            if (File.Exists(suitePath))
            {
                var suite = ExperimentJson.Read<ExperimentSuiteResult>(suitePath);
                if (suite.Status != "completed" || suite.Runs.Length != suite.ExpectedRuns) warnings.Add($"Incomplete suite: {input}");
                paths = suite.Runs.Select(r => Path.Combine(PackageCatalog.FileWithin(input, r), "result.json")).ToArray();
            }
            else
            {
                warnings.Add($"No suite completion record: {input}");
                paths = Directory.EnumerateFiles(input, "result.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            }
            if (paths.Length == 0) warnings.Add($"No results: {input}");
            foreach (string path in paths)
            {
                if (!File.Exists(path)) { warnings.Add($"Missing run result: {path}"); continue; }
                var run = ExperimentJson.Read<ExperimentRunResult>(path);
                if (run.SchemaVersion != 1) throw new InvalidDataException("Unsupported experiment result schema.");
                if (run.Status != "completed") { warnings.Add($"Failed/incomplete run: {path}"); continue; }
                results.Add(run);
            }
        }
        var report = Summarize(results, warnings);
        Directory.CreateDirectory(output);
        ExperimentJson.Atomic(Path.Combine(output, "comparison.json"), report);
        File.WriteAllText(Path.Combine(output, "comparison.csv"), Csv(report));
        File.WriteAllText(Path.Combine(output, "comparison.md"), Markdown(report));
        Console.WriteLine($"COMPARE groups={report.Metrics.Select(m => m.Protocol).Distinct().Count()} metrics={report.Metrics.Length} reproduction={report.Reproductions.Count(r => r.Matches)}/{report.Reproductions.Length} output={output}");
        return warnings.Count > 0 || report.Reproductions.Any(r => !r.Matches) ? 1 : 0;
    }

    public static ComparisonReport Summarize(IReadOnlyList<ExperimentRunResult> input, IEnumerable<string>? warnings = null)
    {
        var notes = (warnings ?? []).ToList();
        var repetitions = new List<ReproductionCheck>();
        var runs = new List<ExperimentRunResult>();
        var collisions = input.GroupBy(r => (r.ProtocolHash, r.Variant)).Where(g => g.Select(r => (r.TreatmentHash, r.ExecutionHash)).Distinct().Count() > 1)
            .Select(g => g.Key).ToHashSet();
        input = input.Select(r => collisions.Contains((r.ProtocolHash, r.Variant))
            ? r with { Variant = r.Variant + "@" + ExperimentJson.Hash(new { r.TreatmentHash, r.ExecutionHash })[..8] } : r).ToArray();
        if (collisions.Count > 0) notes.Add("Reused variant names with different parameters/builds have distinct @ identifiers.");
        foreach (var failed in input.Where(r => r.Status != "completed")) notes.Add($"Incomplete trial excluded: {failed.Variant}/{failed.Seed}.");
        foreach (var group in input.Where(r => r.Status == "completed").GroupBy(r => (r.ProtocolHash, r.Variant, r.Seed, r.WorldSeed)))
        {
            var first = group.First();
            runs.Add(first);
            foreach (var other in group.Skip(1))
            {
                string? difference = ReproductionDifference(first, other);
                repetitions.Add(new(first.Variant, first.Seed, difference is null, difference));
                if (difference is not null) notes.Add($"Repeated seed differs; only the first occurrence contributes to means: {first.Variant}/{first.Seed}.");
            }
        }
        if (runs.Select(r => r.ProtocolHash).Distinct().Count() > 1) notes.Add("Different protocols are reported in separate groups; no pooled ranking is computed.");
        var data = new List<(ExperimentRunResult Run, string Scenario, string Phase, string Measure, string Metric, BehaviorMetric Metadata, double? Value)>();
        foreach (var run in runs)
        {
            var policies = run.Evaluations.Where(e => e.Kind == "policy").ToArray();
            foreach (var scenario in policies.Select(e => e.Scenario).Distinct())
            {
                var final = policies.Where(e => e.Scenario == scenario && e.AtStep == run.TrainingSteps).ToArray();
                var metrics = run.Evaluations.Where(e => e.Scenario == scenario).SelectMany(e => e.Metrics).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
                foreach (var (metric, metadata) in metrics)
                {
                    void Add(string phase, string measure, double? value) => data.Add((run, scenario, phase, measure, metric, metadata, value));
                    Add("final", "quality", ExperimentRunner.Mean(final, metric));
                    var previous = policies.Where(e => e.Phase == scenario && e.Scenario == scenario).ToArray();
                    long previousStep = previous.Select(e => e.AtStep).DefaultIfEmpty(-1).Max();
                    if (previousStep < run.TrainingSteps)
                        Add("final", "retention-change", Difference(ExperimentRunner.Mean(final, metric), ExperimentRunner.Mean(previous.Where(e => e.AtStep == previousStep), metric)));
                    foreach (var phase in policies.Where(e => e.Scenario == scenario).Select(e => e.Phase).Distinct())
                    {
                        var phaseEpisodes = policies.Where(e => e.Scenario == scenario && e.Phase == phase).ToArray();
                        var curve = phaseEpisodes.GroupBy(e => e.AtStep).OrderBy(g => g.Key)
                            .Select(g => (Step: g.Key, Value: ExperimentRunner.Mean(g, metric))).ToArray();
                        foreach (var point in curve) Add(phase, $"curve@{point.Step}", point.Value);
                        Add(phase, "learning-auc", Area(curve));
                        if (phase == scenario)
                        {
                            var baseline = run.Evaluations.Where(e => e.Kind == "frozen" && e.Scenario == scenario && e.Phase == phase);
                            Add(phase, "adaptation-change", Difference(curve.LastOrDefault().Value, ExperimentRunner.Mean(baseline, metric)));
                        }
                    }
                    foreach (string baseline in new[] { "random", "untrained", "frozen" })
                        Add(scenario, baseline, ExperimentRunner.Mean(run.Evaluations.Where(e => e.Scenario == scenario && e.Kind == baseline), metric));
                }
            }
        }
        var summaries = data.GroupBy(d => (d.Run.ProtocolHash, d.Run.Variant, d.Scenario, d.Phase, d.Measure, d.Metric)).Select(group =>
        {
            var metadata = group.First().Metadata;
            var seeds = group.OrderBy(d => d.Run.Seed).Select(d => new SeedValue(d.Run.Seed, d.Run.WorldSeed, d.Value)).ToArray();
            var (n, mean, sd) = Statistics(seeds.Select(s => s.Value));
            return new MetricSummary(group.Key.ProtocolHash, group.Key.Variant, group.Key.Scenario, group.Key.Phase, group.Key.Measure, group.Key.Metric,
                metadata.Unit, metadata.HigherIsBetter, metadata.Proxy, n, mean, sd, seeds);
        }).OrderBy(m => m.Protocol, StringComparer.Ordinal).ThenBy(m => m.Scenario, StringComparer.Ordinal)
            .ThenBy(m => m.Measure, StringComparer.Ordinal).ThenBy(m => m.Metric, StringComparer.Ordinal).ThenBy(m => m.Variant, StringComparer.Ordinal).ToArray();
        var pairs = new List<PairedDifference>();
        foreach (var group in summaries.GroupBy(m => (m.Protocol, m.Scenario, m.Phase, m.Measure, m.Metric)))
        {
            var variants = group.OrderBy(m => m.Variant, StringComparer.Ordinal).ToArray();
            for (int i = 0; i < variants.Length; i++)
            for (int j = i + 1; j < variants.Length; j++)
            {
                var differences = variants[i].Seeds.Join(variants[j].Seeds, s => (s.Seed, s.WorldSeed), s => (s.Seed, s.WorldSeed),
                    (left, right) => Difference(right.Value, left.Value));
                var (n, mean, sd) = Statistics(differences);
                pairs.Add(new(group.Key.Protocol, variants[i].Variant, variants[j].Variant, group.Key.Scenario, group.Key.Phase,
                    group.Key.Measure, group.Key.Metric, n, mean, sd));
            }
        }
        var trials = runs.Select(r => new TrialSummary(r.ProtocolHash, r.Variant, r.Seed, r.WorldSeed, r.BehaviorOutcome,
            r.TrainingSteps, r.Learning.LastOrDefault()?.Controller.Updates ?? 0, r.TrainingSeconds, r.EvaluationSeconds,
            r.Evaluations.Where(e => e.Kind == "policy" && e.AtStep == r.TrainingSteps).Select(e => e.MeanReward).DefaultIfEmpty().Average())).ToArray();
        return new(1, notes.ToArray(), summaries, pairs.ToArray(), repetitions.ToArray(), trials);
    }

    private static double? Difference(double? after, double? before) => after is null || before is null ? null : after - before;
    private static double? Area((long Step, double? Value)[] curve)
    {
        if (curve.Length < 2 || curve.Any(p => p.Value is null)) return null;
        double area = 0;
        for (int i = 1; i < curve.Length; i++) area += (curve[i].Step - curve[i - 1].Step) * (curve[i].Value!.Value + curve[i - 1].Value!.Value) / 2;
        return area / (curve[^1].Step - curve[0].Step);
    }
    private static (int N, double? Mean, double? Sd) Statistics(IEnumerable<double?> source)
    {
        var values = source.Where(v => v is not null).Select(v => v!.Value).ToArray();
        if (values.Length == 0) return (0, null, null);
        double mean = values.Average();
        return (values.Length, mean, values.Length < 2 ? null : Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Length - 1)));
    }

    public static string? ReproductionDifference(ExperimentRunResult left, ExperimentRunResult right, double tolerance = 1e-9)
    {
        if (left.ConfigurationHash != right.ConfigurationHash || left.ExecutionHash != right.ExecutionHash || left.ProtocolHash != right.ProtocolHash ||
            left.TreatmentHash != right.TreatmentHash || left.Seed != right.Seed || left.WorldSeed != right.WorldSeed)
            return "Configuration or execution environment differs.";
        object Comparable(ExperimentRunResult run) => new
        {
            run.Status, run.BehaviorOutcome, run.TrainingSteps, run.TrainingActionHash, run.Evaluations,
            Learning = run.Learning.Select(p => new { p.Step, p.Phase, p.Age, p.Revision, p.Controller, p.MeanReward })
        };
        var a = System.Text.Json.JsonSerializer.SerializeToElement(Comparable(left), ExperimentJson.Lines);
        var b = System.Text.Json.JsonSerializer.SerializeToElement(Comparable(right), ExperimentJson.Lines);
        bool Equal(System.Text.Json.JsonElement x, System.Text.Json.JsonElement y)
        {
            if (x.ValueKind != y.ValueKind) return false;
            if (x.ValueKind == System.Text.Json.JsonValueKind.Number)
                return x.TryGetInt64(out long xi) && y.TryGetInt64(out long yi) ? xi == yi : Math.Abs(x.GetDouble() - y.GetDouble()) <= tolerance;
            if (x.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var properties = x.EnumerateObject().ToArray();
                return properties.Length == y.EnumerateObject().Count() && properties.All(p => y.TryGetProperty(p.Name, out var value) && Equal(p.Value, value));
            }
            if (x.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var first = x.EnumerateArray().ToArray(); var second = y.EnumerateArray().ToArray();
                return first.Length == second.Length && first.Zip(second).All(p => Equal(p.First, p.Second));
            }
            return x.GetRawText() == y.GetRawText();
        }
        return Equal(a, b) ? null : "Actions, counters, metrics or controller diagnostics differ beyond tolerance.";
    }

    private static string Csv(ComparisonReport report)
    {
        static string Escape(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        var result = new StringBuilder("protocol,variant,scenario,phase,measure,metric,unit,proxy,seed,worldSeed,value\n");
        foreach (var metric in report.Metrics)
        foreach (var seed in metric.Seeds)
            result.AppendLine(string.Join(',', new[] { metric.Protocol, metric.Variant, metric.Scenario, metric.Phase, metric.Measure, metric.Metric, metric.Unit,
                metric.Proxy.ToString(), seed.Seed.ToString(CultureInfo.InvariantCulture), seed.WorldSeed.ToString(CultureInfo.InvariantCulture),
                seed.Value?.ToString("R", CultureInfo.InvariantCulture) ?? "" }.Select(Escape)));
        return result.ToString();
    }
    private static string Markdown(ComparisonReport report)
    {
        static string Number(double? value) => value?.ToString("F6", CultureInfo.InvariantCulture) ?? "N/A";
        var text = new StringBuilder("# Experiment comparison\n\nReward is diagnostic and does not determine behavioral success. Proximity measures are proxies.\n\n");
        foreach (string warning in report.Warnings) text.AppendLine("- " + warning);
        if (report.Reproductions.Length > 0) text.AppendLine($"\nReproduced trials: {report.Reproductions.Count(r => r.Matches)}/{report.Reproductions.Length} (tolerance 1e-9).\n");
        foreach (var group in report.Metrics.Where(m => m.Measure is "quality" or "adaptation-change" or "retention-change").GroupBy(m => m.Protocol))
        {
            text.AppendLine($"\n## Protocol {group.Key[..12]}\n\n| Variant | Scenario | Measure | Metric | N | Mean | SD |\n|---|---|---|---|---:|---:|---:|");
            foreach (var m in group.Where(m => m.Metric is "temperature.mae" or "hunger.mean" or "wildlife.proximity"))
                text.AppendLine($"| {m.Variant} | {m.Scenario} | {m.Measure} | {m.Metric}{(m.Proxy ? " (proxy)" : "")} | {m.N} | {Number(m.Mean)} | {Number(m.StandardDeviation)} |");
        }
        text.AppendLine("\nChanges are after minus before; for lower-is-better metrics, negative adaptation means improvement and positive retention change means deterioration. Learning AUC is the trapezoidal area divided by the phase duration. CSV/JSON contain all curves, paired differences and per-training-seed values. Missing measurements are N/A. A short pilot establishes infrastructure behavior, not algorithm superiority.");
        return text.ToString();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using ArtificialLife.Brain;
using ArtificialLife.Core;

namespace ArtificialLife.Application;

public sealed record ControllerSelection
{
    public string Algorithm { get; init; } = "dqn";
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
}

public interface IAgentControllerFactory
{
    string Algorithm { get; }
    string Architecture { get; }
    JsonElement ResolveParameters(JsonElement parameters);
    IAgentController Create(JsonElement parameters, int seed);
    bool Supports(IAgentController controller);
}

/// <summary>Composition root for algorithms. Runtime and experiment code only use Core contracts.</summary>
public static class ControllerRegistry
{
    private static readonly Dictionary<string, IAgentControllerFactory> factories = new(StringComparer.Ordinal)
    {
        ["dqn"] = new DqnControllerFactory(),
        ["relational-sarsa"] = new RelationalSarsaFactory(),
        ["generic-actor-critic"] = new GenericActorCriticFactory()
    };

    public static void Register(IAgentControllerFactory factory)
    {
        if (string.IsNullOrWhiteSpace(factory.Algorithm) || string.IsNullOrWhiteSpace(factory.Architecture) || !factories.TryAdd(factory.Algorithm, factory))
            throw new ArgumentException("Controller identity must be nonempty and unique.");
    }

    public static IAgentControllerFactory Factory(string algorithm) => factories.TryGetValue(algorithm, out var factory)
        ? factory : throw new InvalidDataException($"Unknown learning algorithm: {algorithm}.");

    public static RuntimeOptions Resolve(RuntimeOptions options)
    {
        var selection = options.Controller ?? new ControllerSelection
        {
            Parameters = JsonSerializer.SerializeToElement(new DqnParameters(options.Network, options.Learning), PackageCatalog.Json)
        };
        var parameters = Factory(selection.Algorithm).ResolveParameters(selection.Parameters);
        int seed = options.ControllerSeed ?? (selection.Algorithm == "dqn"
            ? parameters.Deserialize<DqnParameters>(PackageCatalog.Json)!.Learning.Seed : options.Learning.Seed);
        var resolved = options with
        {
            Controller = selection with { Parameters = parameters }, ControllerSeed = seed,
            WorldSeed = options.WorldSeed ?? options.Learning.Seed
        };
        if (selection.Algorithm == "dqn")
        {
            var dqn = parameters.Deserialize<DqnParameters>(PackageCatalog.Json)!;
            dqn = dqn with { Learning = dqn.Learning with { Seed = seed } };
            resolved = resolved with
            {
                Network = dqn.Network, Learning = dqn.Learning,
                Controller = selection with { Parameters = JsonSerializer.SerializeToElement(dqn, PackageCatalog.Json) }
            };
        }
        return resolved;
    }

    public static IAgentController Create(RuntimeOptions options)
    {
        var resolved = Resolve(options);
        return Factory(resolved.Controller!.Algorithm).Create(resolved.Controller.Parameters, resolved.ControllerSeed!.Value);
    }

    public sealed record DqnParameters(NetworkOptions Network, LearningOptions Learning);

    private sealed class GenericActorCriticFactory : IAgentControllerFactory
    {
        public string Algorithm => "generic-actor-critic";
        public string Architecture => GenericActorCriticController.Architecture;
        public JsonElement ResolveParameters(JsonElement parameters)
        {
            var strict = new JsonSerializerOptions(PackageCatalog.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
            var value = parameters.Deserialize<GenericActorCriticOptions>(strict) ?? throw new InvalidDataException("Missing actor-critic parameters.");
            value.Validate(); return JsonSerializer.SerializeToElement(value, PackageCatalog.Json);
        }
        public IAgentController Create(JsonElement parameters, int seed) => new GenericActorCriticController(parameters.Deserialize<GenericActorCriticOptions>(PackageCatalog.Json)!, seed);
        public bool Supports(IAgentController controller) => controller is GenericActorCriticController;
    }

    private sealed class RelationalSarsaFactory : IAgentControllerFactory
    {
        public string Algorithm => "relational-sarsa";
        public string Architecture => RelationalSarsaController.Architecture;
        public JsonElement ResolveParameters(JsonElement parameters)
        {
            var strict = new JsonSerializerOptions(PackageCatalog.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
            var value = parameters.Deserialize<RelationalSarsaOptions>(strict) ?? throw new InvalidDataException("Missing Sarsa parameters.");
            value.Validate(); return JsonSerializer.SerializeToElement(value, PackageCatalog.Json);
        }
        public IAgentController Create(JsonElement parameters, int seed) => new RelationalSarsaController(parameters.Deserialize<RelationalSarsaOptions>(PackageCatalog.Json)!, seed);
        public bool Supports(IAgentController controller) => controller is RelationalSarsaController;
    }

    private sealed class DqnControllerFactory : IAgentControllerFactory
    {
        private static readonly JsonSerializerOptions StrictJson = new(PackageCatalog.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        public string Algorithm => "dqn";
        public string Architecture => DqnController.Architecture;
        public JsonElement ResolveParameters(JsonElement parameters)
        {
            if (parameters.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Controller parameters must be an object.");
            if (parameters.EnumerateObject().Any(p => !p.Name.Equals("network", StringComparison.OrdinalIgnoreCase) && !p.Name.Equals("learning", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("DQN parameters accept network and learning sections only.");
            var value = parameters.Deserialize<DqnParameters>(StrictJson) ?? throw new InvalidDataException("Missing DQN parameters.");
            value = new(value.Network ?? new(), value.Learning ?? new());
            value.Network.Validate(); value.Learning.Validate();
            return JsonSerializer.SerializeToElement(value, PackageCatalog.Json);
        }
        public IAgentController Create(JsonElement parameters, int seed)
        {
            var value = parameters.Deserialize<DqnParameters>(PackageCatalog.Json)!;
            return new DqnController(value.Network, value.Learning with { Seed = seed });
        }
        public bool Supports(IAgentController controller) => controller is DqnController;
    }
}

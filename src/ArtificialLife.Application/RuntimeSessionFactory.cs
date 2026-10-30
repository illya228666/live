using ArtificialLife.Core;

namespace ArtificialLife.Application;

/// <summary>Composition boundary shared by interactive, bounded and research execution.</summary>
public static class RuntimeSessionFactory
{
    public static RuntimeSession Create(RuntimeOptions options, IAgentController? controller = null, PackageCatalog? catalog = null,
        IEnumerable<string>? observationKeys = null, IEnumerable<string>? actionKeys = null, RuntimeWorld? restoredWorld = null,
        IReadOnlyList<ModuleManifest>? restoredModules = null, bool ownsController = true, string? parentSessionId = null, string? trialId = null)
    {
        if (controller is null && options.Mode != RuntimeMode.WorldOnly)
        {
            options = ControllerRegistry.Resolve(options);
            controller = ControllerRegistry.Create(options);
        }
        return new(options, controller, catalog, observationKeys, actionKeys, restoredWorld, restoredModules, ownsController, parentSessionId, trialId);
    }

    public static RuntimeSession LoadPolicy(string directory, RuntimeMode mode = RuntimeMode.Inference) => RuntimeCheckpoint.Open(directory, mode);

    // An inhabited world's controller must be provided explicitly: world checkpoints do not contain its learner state.
    public static RuntimeSession RestoreWorld(string directory, IAgentController? controller = null) => WorldCheckpoint.Open(directory, controller);
}

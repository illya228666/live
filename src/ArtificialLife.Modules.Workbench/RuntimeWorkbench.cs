using ArtificialLife.Core;

namespace ArtificialLife.Modules.Workbench;

/// <summary>
/// Generalization challenge authored after generic-ac-freeze-01. A remotely operated
/// valve changes an observable pressure; it has no homeostatic meaning until upgraded.
/// It is deliberately not a movement action or another contact-consumed resource.
/// </summary>
public sealed class RuntimeWorkbench : IRuntimeModule
{
    private const string State = "workbench.device";
    private bool needEnabled, noiseEnabled;
    public void Register(ModuleBuilder builder)
    {
        needEnabled = builder.Settings.Boolean("needEnabled");
        noiseEnabled = builder.Settings.Boolean("noiseEnabled");
        int gauge = builder.Observations.Register("workbench.reading.v1");
        int noise = builder.Observations.Register("workbench.noise.v1");
        builder.Observations.Register("vision.appearance.workbench.v1");
        int command = builder.Actions.Register("workbench.adjust.v1");
        builder.Components.Add(new(State, 1, (_, _) => false, (_, _, _) => { }));
        builder.Sensors.Add(w =>
        {
            var data = w.State(builder.Manifest.Id);
            ObservationToken[] reading = [new(gauge, [(float)data.Read<double>("pressure")])];
            return noiseEnabled ? reading.Append(new(noise, [(float)data.Read<double>("noise")])) : reading;
        });
        builder.ActionProviders.Add(new(command, _ => [new(command, [-1]), new(command, [1])], (w, action) =>
        {
            var data = w.State(builder.Manifest.Id);
            double before = data.Read<double>("pressure");
            double after = Math.Clamp(before + 0.2 * action.Parameters[0], -1, 1);
            data.Write("pressure", after);
            data.Write("commands", data.Read<long>("commands") + 1);
            string direction = action.Parameters[0] > 0 ? "positive" : "negative";
            data.Write(direction, data.Read<long>(direction) + 1);
            if (Math.Abs(after) < Math.Abs(before)) data.Write("corrective", data.Read<long>("corrective") + 1);
            data.Write("lastEffect", after - before);
            data.Write("pending", true);
        }));
        builder.Systems.Add(new("workbench.drift", w =>
        {
            var data = w.State(builder.Manifest.Id);
            data.Write("pressure", Math.Clamp(data.Read<double>("pressure") +
                (0.015 + 0.025 * Math.Sin(Math.Tau * w.Time / 300)) * w.Options.TimeStep, -1, 1));
            data.Write("acted", data.Read<bool>("pending"));
            data.Write("pending", false);
            if (noiseEnabled) data.Write("noise", 2 * w.Random(builder.Manifest.Id) - 1);
        }, []));
        if (needEnabled)
        {
            builder.Drives.Add(w => [new("pressure", w.State(builder.Manifest.Id).Read<double>("pressure"), 0, 1, 1)]);
            builder.Descriptors.Add(new("pressure", "Pressure stability"));
        }
    }
    public void Migrate(RuntimeWorld world, ModuleManifest? previous)
    {
        var data = world.State("workbench");
        if (previous is null)
        {
            data.Write("pressure", 0.4 * world.Random("workbench") - 0.2);
            data.Write("noise", 0.0);
            data.Write("commands", 0L); data.Write("positive", 0L); data.Write("negative", 0L); data.Write("corrective", 0L);
            var device = world.CreateEntity(new(world.Options.Width / 2, world.Options.Height / 2));
            device.Components[State] = new();
            device.Components["vision.appearance"] = new();
            device.Require("vision.appearance").Write("type", "vision.appearance.workbench.v1");
            device.Require("vision.appearance").Write("diameter", 3.0);
        }
        data.Write("needEnabled", needEnabled); data.Write("noiseEnabled", noiseEnabled);
    }
}

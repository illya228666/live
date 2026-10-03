# Homeostasis reporting

`IDriveProvider[] → Simulation.GetDrives() → HomeostasisReport → samples / metrics / snapshots / HUD / logs`.

Each report copies the currently active drives by key. It records current, target, normalized error `abs(current - target) / scale`, satisfaction `clamp(1 - normalizedError, 0, 1)` and weight. Overall `Balance` is the weighted mean of individual satisfaction values. Zero-weight drives remain visible but do not affect balance; no positive weights means neutral balance 1. This is a state measure, not the transition reward. Reporting does not change reward aggregation, neural observations or learning.

Providers can optionally implement `IDriveDescriptorProvider` with `DriveDescriptor(Key, Name, Unit)`. Descriptors only supply presentation hints. Without a descriptor the HUD displays the key. Temperature supplies `Temperature / °C`, Hunger supplies `Hunger`; the HUD has no cases for either module.

`TrainingSample.Homeostasis` stores a copied keyed report. Balance and reward have separate histories. Drive graphs look up each key, leaving gaps when it was absent. Drives can appear or disappear in successive reports without changing any schema. No module loader is implemented.

`EvaluationMetrics` contains steps, mean balance, mean reward and per-key `DriveMetrics`: sample count, mean current, target, normalized error, satisfaction and weight. A per-key mean includes only samples in which that drive existed. Rolling metrics share the evaluation accumulator and subtract evicted samples; keys disappear from the rolling result after their final sample leaves the window. Old snapshots retain their values.

Both training and checkpoint-view HUDs show overall balance, reward, dynamic NEEDS and balance/reward graphs. Each need has a satisfaction percentage, current/target with optional unit, and a satisfaction history. The list scrolls with the mouse wheel over the right panel when it exceeds three visible rows. Live training additionally shows epsilon, loss, replay, steps, life and age. Body/local temperature remain world debug telemetry at the foot of the map.

Validation: 73 tests pass, including three focused reporting tests for weighted/clamped satisfaction independent of reward/metadata, neutral balance and late-drive propagation through provider/report/history/rolling metrics. The existing learning regression now checks normalized temperature error and overall balance. Godot completed a rendered 5,000-step continuous training smoke; captures and generic metric JSON are in `artifacts/generic-reporting/`.

Previously recorded thermal evaluation JSON and benchmark figures are historical artifacts; newly generated evaluation JSON uses the generic schema.

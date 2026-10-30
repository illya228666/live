# Architecture

All production modes use `RuntimeSessionFactory` and `RuntimeSession.Step` in Application.
World Only omits the controller; Inference/Evaluation disable learning; continuous
Learning keeps the world alive; bounded training passes terminal before replacing
an episode. Research composes phases and independent evaluation worlds over this
same step. The old static orchestration exists only in tests/LegacyReference for
numerical/checkpoint regression; policy v1 import uses a format adapter.

| Project | Responsibility | References |
| --- | --- | --- |
| Core | Physical/module contracts, value state, neutral RuntimeFrame | BCL |
| Modules | Physics, observations, actions and motivation | Core |
| Brain | Controllers, tensors, optimizer and replay | Core, TorchSharp |
| Packages | Catalogue, integrity, import and assembly loading | Core |
| Application | Session factory, execution, policies, evaluations and research | Core, Brain, Packages, Observation.Contracts |
| Hosting | Supervisor/Worker, validation, watchdog, commands and WebSocket | Application, Packages, Observation.Contracts |
| Observation.Contracts | Versioned DTOs and independent client | Core |
| Godot.Contracts | Scoped graphical extensions | Core, Godot SDK |
| Godot | Independent viewer, input, camera and resources | Packages, Observation.Contracts, Godot.Contracts |
| CLI | Launch/control wrappers | Hosting |

See [Runtime/Observation](observation-runtime.md) for protocol, lifecycle and
launch commands, and [module authoring](runtime-modules.md) for activation contracts.

## Numerical/component reference

The descriptions below retain the original physical formulas and legacy CLR type
names. Production packages use `RuntimeWorld` value components and registered
systems. They preserve these formulas; they do not instantiate a second
SimulationSession or TrainingEngine. Module migration initializes new entities;
world restore registers systems against saved state without initialization.

## Hunger and food

`HungerModule` references only Core and implements `IWorldSystem`, `IObservationProvider` and `IDriveProvider`. Its `HungerState.Level` starts at zero, grows linearly by `GrowthPerSecond * elapsedSeconds` and is bounded to [0, 1]. After growth, the system consumes any entity with `Nutrition` at distance <= `EatRadius`, subtracts its `HungerReduction` in percentage points, clamps at zero and removes the entity. Nutrition does not require an appearance, and an appearance does not imply nutrition; there is no special apple type in the module.

The legacy `FoodSpawner : IWorldSystem` corresponds to the production Food module, which composes nutrition and appearance without coupling their modules. `FoodSpawnerOptions` defaults to `SpawnIntervalTicks = 200`, `MaxApples = 8`, and `MinSpawnDistanceFromAgent = 10`. Every interval, capacity permitting, one apple appears at a random world position at least that distance from the current agent. The spawner retains the seeded simulation RNG supplied at reset. Up to 256 rejection attempts prevent restrictive or impossible distances from hanging the simulation; unsuccessful attempts wait for the next interval. Eating never schedules a replacement. Reset clears food and restarts the schedule. Defaults in `HungerOptions` are growth 0.001 per second and eating radius 2. Apples reduce hunger by 0.20 at contact; there is no eating action or direct food reward.

World snapshots copy every existing apple into an immutable value collection and include hunger and total spawned count. Godot draws every apple from this collection on each frame, so removed entities disappear without presentation timers or direct Simulation access.

Hunger emits `hunger.body.v1` with one normalized level feature, and reports a drive with target 0, scale 1 and weight 1. The unchanged `RewardAggregator` averages its normalized error with the temperature drive. Feeding affects reward only through the resulting drive state. Brain/DQN still consumes generic observation tokens and action candidates, with no references to any module.

## Reporting boundary

Core exposes the active providers' `DriveState` values and optional `IDriveDescriptorProvider` metadata separately. Application copies them into `HomeostasisReport`, computes per-key normalized error/satisfaction and weighted overall balance, and routes that immutable report into training samples, rolling/evaluation metrics and world snapshots. The HUD and logs iterate actual keys rather than module names. Metadata never participates in reward. See [homeostasis reporting](homeostasis-reporting.md) for formulas and missing-key history semantics.

## Vision boundary

```text
World entity -> VisualAppearance -> IVisionBackend -> VisualPerception -> VisionModule -> ObservationToken[] -> Brain
             -> HeatEmitter      -> Temperature
```

`IVisionBackend.Perceive` receives world state and `VisionObserver` (position and heading), and returns only neutral appearance type, bearing relative to the observer's forward axis, distance and apparent angular diameter. The output contains no absolute position, entity reference, identity or heat semantics. A raycasting backend can implement the same interface without changing Vision's observation encoding or Brain.

The first backend uses direct geometry and considers every entity with a visual appearance visible. It transforms displacement into the observer frame and computes `atan2(lateral, forward)` and `2 * atan2(diameter / 2, distance)`. At coincident positions the bearing is zero and apparent size is pi. There is no occlusion, field of view, range filter or turning action.

Appearance selects a neutral registry key (`vision.appearance.disc.v1` or `vision.appearance.diamond.v1`); four numeric features are `cos(bearing)`, `sin(bearing)`, `distance / (1 + distance)` and `angularDiameter / pi`. They fit the existing network width and stay within [-1, 1]. Direction is continuous across the angle wrap; distance normalization uses no world size or global coordinate. The initial default state contains eight tokens: six thermal, one visual fire and one hunger. Each spawned/eaten apple adds/removes one visual token. Brain consumes only generic tokens and candidates, and references no module.

The added observation keys require retraining older policies: checkpoint registry validation rejects pre-vision and pre-hunger checkpoints. New saves and loads use the existing format. The episodic CLI benchmark, seeds, thermal physics and network dimensions remain available; earlier measured results describe the earlier observation set and temperature-only drive.

## One simulation step

1. Query legal candidates from action providers.
2. Select a candidate with the neural policy or uniform exploration.
3. Validate the candidate against the current legal set.
4. Read previous normalized drive error.
5. Execute movement, advance the fixed clock and update world systems.
6. Read new drive error; aggregate and clamp reward in Core.
7. Produce new observations, legal actions and terminal status.

Diagonal movement is normalized to the same speed as cardinal movement. Boundary-crossing candidates are omitted instead of clamped into duplicate actions. Stay is always legal. Public `Simulation.Step` rejects fabricated candidates as well as invalid boundary moves.

Headless training uses 400-step episodes, randomized starting positions, body temperatures and field phases. Replay receives the final state and terminal flag before the next reset. Those benchmark time-limit transitions intentionally do not bootstrap. Live Training in the Host uses a continuous lifecycle, so these limits produce neither terminal transitions nor resets. Frozen checkpoint visualization also runs continuously.

## Presentation and execution ownership

The Host owns the mutable world/controller on one thread. Commands are applied at
step boundaries. Network tasks serialize shared immutable publications and send
them through one sender per connection. No observers means no network snapshots;
heartbeat and required research measurements remain independent.

Godot runs as a separate process. Its WebSocket client receives full state and
resources, then changes Godot objects only on the main thread. It interpolates
presentation positions without advancing physics. Closing Viewer or Launcher
leaves Host running. Frame rate, observation rate and simulation pacing are separate.
There is no simulation work budget in Godot.

Temporary evaluation forks a frozen controller and creates an explicitly linked
child session. The original learner/world stays suspended and resumes intact.
Policy checkpoints do not promise optimizer/replay/controller RNG recovery;
Learning state loss ends the run explicitly. C# graphical upgrades can require a
Viewer restart independently of the world.

## Reproducibility

World resets use a managed seed. Torch initialization, epsilon exploration and replay sampling are seeded. Torch runs on a single CPU thread. Bit-identical results across operating systems/native library versions are not promised; tool and package pins narrow that variability.

Checkpoints record ordered logical type keys and all configuration. SHA256 detects a corrupt or mismatched weights file. Current policy/world formats publish immutable generations through an atomic checksummed pointer; the v1 read adapter retains legacy validation. Checkpoints contain inference weights, not optimizer/replay state.

NuGet lock files were generated for the verified Windows platform. A future cross-platform CI matrix should maintain separate platform lock files before claiming locked Linux reproducibility.

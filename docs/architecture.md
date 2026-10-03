# Architecture

## Responsibility and dependency boundaries

The simulation can run without Godot. A future server would use the same `SimulationSession`, action selection and physics; it would replace the current presentation adapter. No engine lifecycle or pixel coordinate participates in training.

| Project | Responsibility | References |
| --- | --- | --- |
| Core | Spatial world, entity component storage, contracts, legal movement, reward | BCL |
| Modules.Temperature | Thermal body/field, sensory tokens and drive | Core |
| Modules.Vision | Visual appearance, replaceable backend, relative perceptions and sensory tokens | Core |
| Modules.Hunger | Bounded hunger, nutrition/contact feeding, sensory token and drive | Core |
| Brain | Generic tensors, Q model, replay and learning | Core, TorchSharp/LibTorch |
| Application | Composition, configuration, training/evaluation, checkpoint schema, snapshots | Core, Brain, Temperature, Vision, Hunger |
| Cli | Command parsing and console diagnostics | Application |
| Godot | Rendering, input and human-speed stepping | Application, Godot SDK |

There is no service container, event bus or runtime DLL loader. The composition root is `SimulationSession`. A module can implement the contracts it actually needs; it need not be a monolithic plugin object.

`Entity` stores a position and module-owned components by CLR type. `AgentState` extends it with an observer orientation in radians; movement does not rotate the body. `ThermalBody` belongs to the temperature module, so Core has no body-temperature field. `WorldState` contains spatial limits, a clock and an entity collection. Providers receive this context and the relevant agent.

`SimulationSession` creates one fire entity with independent `HeatEmitter` and `VisualAppearance(Disc)` components. Temperature sums the fields of all heat emitters at their entity positions. It creates no fire and assumes no source location. Snapshots read the same fire entity. The root registers `FoodSpawner` and creates no initial food. Spawned apples carry `Apple`, `Nutrition(0.20)` and `VisualAppearance(Diamond)` components.

`Simulation` receives the initial entity collection and restores its membership on explicit episode reset. Module systems reset their own mutable components; hunger and the agent orientation reset to zero. Continuous life has no automatic world reset or starvation death. Food arrives on an independent tick schedule.

## Hunger and food

`HungerModule` references only Core and implements `IWorldSystem`, `IObservationProvider` and `IDriveProvider`. Its `HungerState.Level` starts at zero, grows linearly by `GrowthPerSecond * elapsedSeconds` and is bounded to [0, 1]. After growth, the system consumes any entity with `Nutrition` at distance <= `EatRadius`, subtracts its `HungerReduction` in percentage points, clamps at zero and removes the entity. Nutrition does not require an appearance, and an appearance does not imply nutrition; there is no special apple type in the module.

`FoodSpawner : IWorldSystem` lives in Application, where nutrition and appearance can be composed without coupling their modules. `FoodSpawnerOptions` defaults to `SpawnIntervalTicks = 200`, `MaxApples = 8`, and `MinSpawnDistanceFromAgent = 10`. Every interval, capacity permitting, one apple appears at a random world position at least that distance from the current agent. The spawner retains the seeded simulation RNG supplied at reset. Up to 256 rejection attempts prevent restrictive or impossible distances from hanging the simulation; unsuccessful attempts wait for the next interval. Eating never schedules a replacement. Reset clears food and restarts the schedule. Defaults in `HungerOptions` are growth 0.001 per second and eating radius 2. Apples reduce hunger by 0.20 at contact; there is no eating action or direct food reward.

World snapshots copy every existing apple into an immutable value collection and include hunger and total spawned count. Godot draws every apple from this collection on each frame, so removed entities disappear without presentation timers or direct Simulation access.

Hunger emits `hunger.body.v1` with one normalized level feature, and reports a drive with target 0, scale 1 and weight 1. The unchanged `RewardAggregator` averages its normalized error with the temperature drive. Feeding affects reward only through the resulting drive state. Brain/DQN still consumes generic observation tokens and action candidates, with no references to any module.

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

Headless training uses 400-step episodes, randomized starting positions, body temperatures and field phases. Replay receives the final state and terminal flag before the next reset. Those benchmark time-limit transitions intentionally do not bootstrap. Godot Live Training uses a continuous lifecycle, so these limits produce neither terminal transitions nor resets. Frozen checkpoint visualization also runs continuously.

## Presentation boundary

`VisualizationSession` owns a frozen policy and simulation. It returns immutable `WorldViewModel`, `AgentViewModel` and `HeatSourceViewModel` records containing only primitives. The Godot script maps those numbers into its viewport and maintains cosmetic trails and a history chart. Rendering cannot affect physical state or rewards.

The Godot adapter advances fixed simulation steps using a wall-clock accumulator. The headless trainer has no accumulator or delays. Engine framerate therefore never affects the experiment's physical timestep.

## Incremental live training

`TrainingEngine.AdvanceOne` contains the shared transition logic: epsilon selection, simulation step, replay insertion, DQN update and reset on terminal. `SimulationLifecycle` explicitly distinguishes `Episodic` (the default for CLI training) from `Continuous` (selected by `TrainingVisualizationSession`). Only episodic simulations produce terminal transitions at `EpisodeSteps`. Continuous simulations initialize once in `SimulationSession`; the engine neither initializes a second body nor resets at the old boundaries. `Trainer.Run` retains its loop/logging. Live training uses the same engine, with rolling metrics, bounded histories and copied read-only snapshots.

Godot can request many training steps without changing the physical timestep. It caps each batch and checks a time budget between complete steps, then returns to input/render processing. All mutable brain/world state has one synchronous owner on the Godot thread; rendering receives primitive records, not tensor aliases or mutable simulation references. Closing the node disposes the Application session and its brain.

Episodic terminal samples are recorded before reset. Live training has no automatic terminal/reset boundary: world time, position, body temperature, fire phase, brain, optimizer, replay and epsilon progression continue through the entire life. Its HUD shows Life, Age and TrainingStep; histories evict old samples without clearing at `EpisodeSteps`. Ctrl + Shift + R disposes the old brain/optimizer and creates a new seeded brain, replay, body and world, resets training counters and increments the life number. Frozen evaluation uses a separate continuous simulation and a greedy scorer that does not consume the training exploration/replay RNG; training state is suspended intact. Histories clear on mode switches and new life.

The checkpoint schema stays at version 1. Its existing `TrainingSteps` metadata now accepts an explicit completed count when live training saves; the configured headless step budget remains in the original configuration. CLI evaluation reads the completed count from that metadata instead of reporting a planned training budget as completed work.

## Reproducibility

World resets use a managed seed. Torch initialization, epsilon exploration and replay sampling are seeded. Torch runs on a single CPU thread. Bit-identical results across operating systems/native library versions are not promised; tool and package pins narrow that variability.

Checkpoints record ordered logical type keys and all configuration. SHA256 detects a corrupt or mismatched weights file. Individual files are replaced through temporary paths; the pair is not a transactional filesystem object, but checksum validation detects an interrupted inconsistent save. Checkpoints contain inference weights, not optimizer/replay state.

NuGet lock files were generated for the verified Windows platform. A future cross-platform CI matrix should maintain separate platform lock files before claiming locked Linux reproducibility.

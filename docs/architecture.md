# Architecture

## Responsibility and dependency boundaries

The simulation can run without Godot. A future server would use the same `SimulationSession`, action selection and physics; it would replace the current presentation adapter. No engine lifecycle or pixel coordinate participates in training.

| Project | Responsibility | References |
| --- | --- | --- |
| Core | Spatial world, entity component storage, contracts, legal movement, reward | BCL |
| Modules.Temperature | Thermal body/field, sensory tokens and drive | Core |
| Brain | Generic tensors, Q model, replay and learning | Core, TorchSharp/LibTorch |
| Application | Composition, configuration, training/evaluation, checkpoint schema, snapshots | Core, Brain, Temperature |
| Cli | Command parsing and console diagnostics | Application |
| Godot | Rendering, input and human-speed stepping | Application, Godot SDK |

There is no service container, event bus or runtime DLL loader. The composition root is `SimulationSession`. A module can implement the contracts it actually needs; it need not be a monolithic plugin object.

`AgentState` stores module-owned components by CLR type. `ThermalBody` belongs to the temperature module, so Core has no body-temperature field. `WorldState` contains spatial limits and a clock. Providers receive this small context and the relevant entity, not an all-purpose service locator.

## One simulation step

1. Query legal candidates from action providers.
2. Select a candidate with the neural policy or uniform exploration.
3. Validate the candidate against the current legal set.
4. Read previous normalized drive error.
5. Execute movement, advance the fixed clock and update world systems.
6. Read new drive error; aggregate and clamp reward in Core.
7. Produce new observations, legal actions and terminal status.

Diagonal movement is normalized to the same speed as cardinal movement. Boundary-crossing candidates are omitted instead of clamped into duplicate actions. Stay is always legal. Public `Simulation.Step` rejects fabricated candidates as well as invalid boundary moves.

Training uses 400-step episodes, randomized starting positions, body temperatures and field phases. Replay receives the final state and terminal flag before the next reset. Time-limit transitions intentionally do not bootstrap. Visualization ignores episode resets and runs continuously.

## Presentation boundary

`VisualizationSession` owns a frozen policy and simulation. It returns immutable `WorldViewModel`, `AgentViewModel` and `HeatSourceViewModel` records containing only primitives. The Godot script maps those numbers into its viewport and maintains cosmetic trails and a history chart. Rendering cannot affect physical state or rewards.

The Godot adapter advances fixed simulation steps using a wall-clock accumulator. The headless trainer has no accumulator or delays. Engine framerate therefore never affects the experiment's physical timestep.

## Reproducibility

World resets use a managed seed. Torch initialization, epsilon exploration and replay sampling are seeded. Torch runs on a single CPU thread. Bit-identical results across operating systems/native library versions are not promised; tool and package pins narrow that variability.

Checkpoints record ordered logical type keys and all configuration. SHA256 detects a corrupt or mismatched weights file. Individual files are replaced through temporary paths; the pair is not a transactional filesystem object, but checksum validation detects an interrupted inconsistent save. Checkpoints contain inference weights, not optimizer/replay state.

NuGet lock files were generated for the verified Windows platform. A future cross-platform CI matrix should maintain separate platform lock files before claiming locked Linux reproducibility.

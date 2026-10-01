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

Training uses 400-step episodes, randomized starting positions, body temperatures and field phases. Replay receives the final state and terminal flag before the next reset. Time-limit transitions intentionally do not bootstrap. Frozen checkpoint visualization runs continuously; Live Training preserves the trainer's episode resets.

## Presentation boundary

`VisualizationSession` owns a frozen policy and simulation. It returns immutable `WorldViewModel`, `AgentViewModel` and `HeatSourceViewModel` records containing only primitives. The Godot script maps those numbers into its viewport and maintains cosmetic trails and a history chart. Rendering cannot affect physical state or rewards.

The Godot adapter advances fixed simulation steps using a wall-clock accumulator. The headless trainer has no accumulator or delays. Engine framerate therefore never affects the experiment's physical timestep.

## Incremental live training

`TrainingEngine.AdvanceOne` now contains the original CLI transition logic: epsilon selection, simulation step, replay insertion, DQN update and deterministic episode reset. `Trainer.Run` retains its loop/logging but delegates each transition to that engine. `TrainingVisualizationSession` owns a brain and the same engine, supplies rolling metrics, bounded histories and copied read-only snapshots, and exposes incremental batches, complete reset, checkpoint save and frozen evaluation.

Godot can request many training steps without changing the physical timestep. It caps each batch and checks a time budget between complete steps, then returns to input/render processing. All mutable brain/world state has one synchronous owner on the Godot thread; rendering receives primitive records, not tensor aliases or mutable simulation references. Closing the node disposes the Application session and its brain.

Terminal samples are recorded before reset. The snapshot may show the new episode at step zero, while the graph retains actual previous transition samples. The trail filters episode identity so a reset never becomes a movement line. Frozen evaluation uses a separate simulation and a greedy scorer that does not consume the training exploration/replay RNG; training state is suspended intact.

The checkpoint schema stays at version 1. Its existing `TrainingSteps` metadata now accepts an explicit completed count when live training saves; the configured headless step budget remains in the original configuration. CLI evaluation reads the completed count from that metadata instead of reporting a planned training budget as completed work.

## Reproducibility

World resets use a managed seed. Torch initialization, epsilon exploration and replay sampling are seeded. Torch runs on a single CPU thread. Bit-identical results across operating systems/native library versions are not promised; tool and package pins narrow that variability.

Checkpoints record ordered logical type keys and all configuration. SHA256 detects a corrupt or mismatched weights file. Individual files are replaced through temporary paths; the pair is not a transactional filesystem object, but checksum validation detects an interrupted inconsistent save. Checkpoints contain inference weights, not optimizer/replay state.

NuGet lock files were generated for the verified Windows platform. A future cross-platform CI matrix should maintain separate platform lock files before claiming locked Linux reproducibility.

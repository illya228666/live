# Live Training

`./tools/run-godot.ps1 -Train` starts one continuous life. The agent, thermal body and world initialize once. Position, body temperature, world time and fire phase continue through every former `EpisodeSteps` boundary. The brain, optimizer, replay and epsilon schedule also continue; transitions at those boundaries are nonterminal.

Headless `./tools/train.ps1` remains episodic. Its configuration, deterministic episode seeds, replay/learning order and measured benchmark are unchanged. `EpisodeSteps` remains a real benchmark setting, rather than being replaced with an artificial large limit.

## Lifecycle and ownership

`SimulationLifecycle` lives in Core and defaults to `Episodic`. `SimulationSession` passes the selected lifecycle into `Simulation`. Episodic simulations return terminal at `EpisodeSteps`; continuous simulations do not. Both CLI and live training use the same `TrainingEngine.AdvanceOne()` for action selection, physical steps, replay insertion and DQN updates. The engine resets only after a terminal transition and does not initialize a continuous session a second time.

`TrainingVisualizationSession` selects `Continuous` and owns the model, optimizer, replay, simulation and histories on the Godot thread. Godot requests bounded batches and renders copied immutable snapshots. Closing the node disposes the session and native brain.

The HUD shows Life, Age and TrainingStep. Age counts physical steps of the training life; training counters remain visible while frozen evaluation displays its independent world. Life starts at 1 and increments on manual restart. Age, training step, epsilon progression, optimizer counters and rolling metrics restart with each new life.

## Controls and rates

| Input | Action |
| --- | --- |
| Space | Pause/resume advancing work |
| + / - | Switch 1x, 10x, 100x, Max |
| E | Suspend training and run the greedy policy in a separate world; press again to resume |
| S | Save inference weights and actual completed training step count |
| Ctrl + Shift + R | Start a new life with fresh weights, optimizer, replay, body, world and counters |

Ctrl + Shift + R is the only runtime action that restarts the training life. Plain R does not reset it. Restart uses the configured seed, so newly initialized weights and physical state reproduce the original start; learned weights are discarded. Frozen evaluation neither changes training state nor consumes its RNG. Histories clear on evaluation mode switches to avoid joining separate worlds.

`configs/live-training.json` separates presentation settings from physics/DQN configuration. Defaults request 60, 600 or 6,000 steps/s, with batches capped at 256 and an 8 ms cooperative budget. Max requests bounded work without a rate limiter. Rates never change simulation `TimeStep`.

The trail holds up to 180 actual positions. Graphs hold up to 240 samples, normally every second physical step. Metrics cover the most recent 1,000 transitions. Old samples are evicted; none of these collections clear at former episode boundaries.

Live training continues until paused or closed. `Learning.TrainingSteps` remains the headless budget. S saves to `artifacts/checkpoints/live-thermal` unless `-Checkpoint` selects another directory. Checkpoint format remains version 1 and stores inference weights, without optimizer/replay resumption or whole-life persistence.

## Verification

All 37 tests pass. Lifecycle checks cover:

- 1,201 continuous transitions across boundaries 400, 800 and 1,200, with no terminal transition in either samples or any replay slot.
- One thermal body instance, exact time progression, movement bounded by speed and exact thermal recurrence on every step.
- Replay retention, optimizer update counts and uninterrupted epsilon progression.
- Histories spanning multiple former episode boundaries.
- Full restart of world, weights, optimizer, replay, counters and histories.
- Headless weights matching the original episodic loop, plus the existing learning regression.

```powershell
./tools/test.ps1
./tools/run-godot.ps1 -Train -Smoke -SmokeSteps 40000 -Checkpoint artifacts/checkpoints/continuous-smoke
./tools/train.ps1 -Checkpoint artifacts/checkpoints/episodic-lifecycle-validation
```

Godot smoke completed 40,000 actual live training transitions in Life 1, with 9,751 optimizer updates and 40,000 replay entries. Smoke checks every retained transition without gaps, rejecting terminal boundaries, teleportation, thermal discontinuity and time/counter/replay divergence. It also exercises the Godot key input path: plain R preserves the life; Ctrl + Shift + R creates a fresh one.

A separate rendered Godot run completed the same 40,000 steps with continuity checks and inspected viewport captures. The HUD remained Life 1, Age 40,000, TrainingStep 40,000. Recent training-window results were:

| Training step | Mean absolute error | Comfort | Epsilon |
| --- | --- | --- | --- |
| 1,000 | 12.744 C | 0% | 0.981 |
| 20,100 | 11.686 C | 0% | 0.6181 |
| 40,000 | 0.209 C | 98.7% | 0.24 |

Learning is not monotonic, but the final window demonstrates regulation within one continuous life. These are exploratory training-window metrics, not held-out evaluation. Captures and JSON observations are generated under `artifacts/continuous-lifecycle/`; live weights are under `artifacts/checkpoints/continuous-rendered/`.

The 100,000-step episodic headless rerun exactly reproduces every aggregate and per-seed metric in `docs/experiment-results.json`: trained MAE 0.32747596330401163 C, comfort 96.5375%, reward 0.9148622877758323. Validation uses a separate checkpoint directory and preserves the existing measured artifacts.

## Performance limits

The budget is checked between complete training steps. A native optimizer call, first-use initialization, capture or checkpoint write can exceed it. Higher speeds skip intermediate rendered frames, while trails and graphs still use actual transitions. Saving synchronously may briefly pause presentation.

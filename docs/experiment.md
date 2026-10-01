# Measured experiment

Run on 2026-10-02, Windows x64, CPU inference/training with one LibTorch thread. No GPU training or Godot process was used by the trainer. Tool and package pins are listed in the README.

## Protocol

- Configuration: `configs/default.json`, training seed 42.
- Training: 100,000 world steps, 400-step episodes, approximately 24,751 optimizer updates.
- Epsilon: linear 1 → 0.05 over 50,000 steps, then held at 0.05.
- Evaluation: seeds 1001, 2002, 3003, 4004, 5005; four episodes per seed, 400 steps each.
- Episode initialization: seed + episode × 10,000. None of these initial-state seeds is used by the training episodes (42 through 291).
- Each policy receives identical initial physical states. Random baseline chooses uniformly among legal actions. Neural policies use ε = 0.
- All 8,000 steps count, including recovery from unfavorable starting positions. Comfort is body target ±1 °C.

## Results

| Policy | Mean absolute body error | Time comfortable | Mean step reward |
| --- | ---: | ---: | ---: |
| Uniform random legal action | 9.089 °C | 5.39% | -0.5525 |
| Initialized neural network, greedy | 14.747 °C | 0.48% | -0.9369 |
| Trained neural network, greedy | **0.327 °C** | **96.54%** | **+0.9149** |

Mean error fell by approximately **96.4%** relative to random. The trained mean body temperature was 36.630 °C.

| Initial-state seed | Random error | Trained error | Trained comfort |
| --- | ---: | ---: | ---: |
| 1001 | 12.200 °C | 0.473 °C | 94.63% |
| 2002 | 6.511 °C | 0.157 °C | 98.69% |
| 3003 | 11.790 °C | 0.591 °C | 94.81% |
| 4004 | 6.472 °C | 0.165 °C | 97.63% |
| 5005 | 8.475 °C | 0.251 °C | 96.94% |

Training itself took **44.54 seconds**, about 2,245 simulation steps/s on this development PC. One step advances one simulated second, so the trainer was over 2,000× faster than real time. This timing excludes build, baseline evaluation and final evaluation; it is not a performance guarantee for other PCs.

The full-precision report is saved beside the generated checkpoint at `artifacts/checkpoints/thermal/evaluation.json`. A copy is committed as [experiment-results.json](experiment-results.json); generated weights and binaries are ignored by Git. Re-running training regenerates the checkpoint locally.

## What the checks establish

The policy succeeds on independent initial states over multiple changing heat-field cycles. The untrained greedy policy performs worse than random, which is a useful reminder that a running neural network is not a learned controller. Both frozen baselines and the trained policy use the same observation and legal-action interfaces.

A separate learning test trains for 30,000 steps and evaluates seeds 606, 707 and 808, two episodes each. It requires error below half the random baseline, at least 30 percentage points more comfort, and a mean reward improvement over 0.3. It does not assert a favorable loss value. The full suite also verifies temporary tensor alias counts remain stable across ragged training batches.

The Godot runtime smoke check loads the saved weights and advances 1,000 continuous steps. An actual GPU-rendered window capture, rather than a mock image, is in `docs/images/thermoregulation.png`.

## Limits of this evidence

This is one primary training seed with several evaluation seeds, plus a shorter automated training regression. It does not measure reliability across a large distribution of training seeds, observation noise, changed physical coefficients or additional competing drives. A clean source-copy workflow on this PC downloaded both pinned tools, passed all 26 tests, retrained in 46.5 seconds with exactly matching evaluation metrics, and passed the Godot load/1,000-step smoke check. This was not a pristine Windows VM. The GitHub Actions workflow is prepared but has not been run on a remote repository from this workspace.

## Best next experiment

Add Fruit + Hunger as a statically composed second module. Keep the same network dimensions, reserve new observation/action slots, train with both drives and report the comfort–hunger tradeoff against single-drive and random baselines. No Fruit or Hunger implementation is included here.

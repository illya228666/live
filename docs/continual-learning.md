# Continual-learning acceptance: 22 October 2026

**Passed, across all five predeclared training seeds.** The measured result establishes live installation and subsequent learning in the controlled scenario below. It does not establish mastery of arbitrary future modules or balance in the original 100 × 100 default world.

The complete configuration and per-seed metrics are retained in [continual-results.json](continual-results.json); the runnable configuration is [configs/continual.json](../configs/continual.json).

## Procedure

For each training seed 42, 73, 101, 211 and 509, train one continuous agent for 100,000 steps with Temperature, Vision, Movement and Homeostasis. Install Hunger and Food through the real package loader, without resetting world/body, weights, target network, Adam or age, then train the same life for another 200,000 steps. Counters advance from age 100,000 to 300,000 and optimizer updates 24,751 to 74,502 for every seed.

Evaluate four cases in independent worlds: pretrained thermal policy before installation, frozen policy at installation, random legal actions after installation, and the adapted policy. Every evaluation uses the fixed held-out world seeds 1001, 2002, 3003, 4004 and 5005 for 4,000 continuous steps each. No evaluation steps update the training model, replay or training RNG. The exported frozen inference mask excludes newly introduced types, so it retains the policy that actually existed at installation.

Pilots used only training-derived evaluation seeds. Configuration and thresholds were fixed before any held-out evaluation. The five independent training runs were executed in parallel processes and aggregated with `ContinualExperiment.Summarize`; the report's `Seconds` is the sum of their individual wall times. A sequential invocation with the full configuration follows the same per-seed procedure.

## Controlled environment and training

This experiment uses the existing mechanics with different settings: a 40 × 40 world, ambient 34.1 °C, body heat production 0.3, a localized heat source with radius 3 and oscillation amplitude 0.15, and the original 36.6 °C target/drive scale 6. The broad comfortable region and localized heat hazard make retained thermal regulation compatible with food seeking. One apple at a time arrives every 100 steps, at least four units from the agent; contact eating radius is three and hunger drive weight is four. Hunger grows by 0.001 per physical second. There is no food bonus or new gameplay mechanic.

The network keeps the original Deep Sets/token/candidate-scoring structure and default sizes: 256 type slots, width four, eight-dimensional embeddings, state size 32 and hidden size 64. Learning uses Double DQN, learning rate 0.001, gamma 0.97, target sync every 100 optimizer updates, epsilon floor 0.3, a temporary 0.5 exploration floor after installation and 10,000-step introduction of new observation types after warm-up. TD-error-prioritized replay uses alpha 0.6 and importance beta 0.4. Uniform replay remains the normal default. The sampling method follows the general approach of [Prioritized Experience Replay](https://arxiv.org/abs/1511.05952); this implementation retains the existing model and optimizer.

## Results

All figures below are mean normalized errors; lower is better.

| Training seed | Hunger, frozen | Hunger, random | Hunger, adapted | Temperature, adapted |
| --- | ---: | ---: | ---: | ---: |
| 42 | 0.875125 | 0.853005 | 0.658564 | 0.001919 |
| 73 | 0.875125 | 0.853005 | 0.454393 | 0.016495 |
| 101 | 0.875125 | 0.853005 | 0.277778 | 0.006701 |
| 211 | 0.875125 | 0.853005 | 0.426600 | 0.010394 |
| 509 | 0.875125 | 0.853005 | 0.220604 | 0.009780 |

Aggregate hunger error is 53.43% below frozen and 52.22% below random, exceeding the required 20% reductions. Aggregate temperature error is 0.009058, below the required `max(1.25 × frozen error, 0.05) = 0.05`. With fixed thermal scale six, this is about 0.054 °C mean absolute body-temperature error. Each seed also satisfies both hunger comparisons and the thermal limit individually.

Initial pilots with uniform replay failed the behavioral threshold, including tests in the original world. That negative result is why the sampler and introduction strategy are explicit settings and why this report limits its claim to the measured scenario. No failed run or training seed was replaced in the final five-seed evaluation.

## Reproduce and inspect

```powershell
./tools/build.ps1 -Headless
. ./tools/common.ps1
Initialize-Dotnet
Invoke-Dotnet run --project src/ArtificialLife.Cli -c Release --no-build -- continual --config configs/continual.json --output artifacts/continual-report.json
```

The experiment exports pretrained, frozen and adapted policies under `artifacts/continual-policies-*/<seed>-*`. They include archived package versions and inference state and can be opened with `tools/run-godot.ps1 -Checkpoint PATH` or evaluated with `tools/evaluate.ps1 -Checkpoint PATH`. These exports start a new evaluation world; they do not restore a suspended training life.

The separate `tools/runtime-smoke.ps1` validates package addition, state-preserving physics updates, presentation-only updates without a new learning revision, and actual collection of retired simulation/Godot assembly contexts. Unit/integration tests additionally cover rejected activations, schemas, capacity, action extension/filtering, revision-isolated replay, export corruption, v1 import and bounded continuous snapshots.

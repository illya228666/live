# ArtificialLife

Live is an experimental, extensible virtual universe inhabited by a continuously
learning agent. The current [research direction](docs/research-direction.md) tests
adaptation to new observations, needs and interactions during one life, including
autonomous exploration before an interaction has homeostatic value. No particular
learning algorithm is required. Ecology-v2 and earlier results remain comparative
references; the former safety-weight continuation has been superseded.

The [first expanding-life experiment](docs/expanding-life-first-results.md) is now
implemented and reproduced: nine continuous lives, a new interaction before need
value, and later adaptation. The first generic actor-critic failed its advancement
rule; increased interaction coverage did not establish reliable learning or retention.

The [follow-up capability diagnostics](docs/signal-diagnostic-report.md) keep that
brain unchanged. Across54 executions, new actions can be learned, but channel
changes, irrelevant observations, old-policy retention and useful experience before
need value expose distinct limits. These are open diagnostics, not new evidence
of generalization; the report fixes the next bounded research decision.

The [interim research report (26 October 2026)](docs/research-local-interim-report.md)
records 11 completed studies and 82 training runs on ecology-v2. The agent learned
substantial joint food/temperature/avoidance behavior, but independent confirmation
failed the preregistered reliability rule (4/5 training seeds, 18/25 evaluation worlds).
The report includes negative results, implementation limits and reproduction commands.

The preserved validated [ecology-v2 reference](configs/research-ecology-v2.json) covers
unchanged thermal-v2, foraging and avoidable wildlife proximity. See the
[five-seed passive/active audit](docs/ecology-v2-report.md). Standalone thermal-v2,
hunger-v2 and safety-v2 remain available through `research/baseline.json`.
The preserved [thermal-v2 baseline](configs/research-baseline.json) has its own
[scenario validation](docs/temperature-scenario-v2-report.md), which excludes permanent
comfort at any fixed position and demonstrates sensor-based active regulation.
Additional factors require their own suitability checks and repairs before research.

For local autonomous research in Codex, start with the [campaign guide](research/README.md).
It adds durable research memory, frozen preregistrations, bounded Windows execution,
stop/resume and artifact backups on top of the existing experiment runner.

Headless Motivation × Learning studies now have configurable variants, fixed phases/seeds,
independent behavioral evaluation, policy exports and comparison reports. See the
[experiment guide](docs/experiments.md) and `configs/experiments-pilot.json`.
The [reproducibility pilot](docs/experiments-pilot-report.md) records two complete
12-trial series, including negative behavioral results.
The [Drive Motivation study protocol](docs/drive-motivation-protocol.md) fixes an
unweighted L2 Drive Reduction / Drive Level comparison against Weighted Homeostasis,
with one shared Double DQN configuration and independent temporal behavior metrics.
The [completed five-seed comparison](docs/drive-motivation-report.md) separates
technical correctness from hunger, temperature, boar proximity and retention results.

A small simulated creature learns to regulate its own body temperature in a changing 2D thermal environment. Its brain is a local TorchSharp neural network trained through reinforcement learning, entirely in C#.

The central question is: **which combinations of motivation and learning sustain
practical adaptation as the world expands, without mechanic-specific brain code?**

The primary runtime now supports one continuous life with hot-installed/upgraded DLL packages. Temperature, vision, hunger, food supply, movement, motivation and Godot presentation use runtime contracts. The episodic training policy uses the same modular execution pipeline. Continual-learning acceptance is measured separately; runtime plumbing alone does not establish that a newly installed mechanic has been learned.

```powershell
./tools/build.ps1
./tools/live.ps1                         # headless continuous learning
./tools/run-godot.ps1 -Train             # separate Host and Viewer
./tools/activate.ps1 -Package hunger@1.0.0,food@1.0.0
./tools/runtime-smoke.ps1                # runtime checks and protected Host/Viewer smoke
```

`configs/runtime.json` selects startup packages; the existing `configs/default.json` remains supported through an adapter. To install Hunger/Food later, start a configuration without those packages. Use exact new package versions for upgrades. See [runtime module authoring and guarantees](docs/runtime-modules.md) for state migration, activation requests, extensible presentation, motivation and controller contracts.

The standard package set includes the [wild boar](docs/wildlife.md): one wandering
animal, its external sprite, danger radius and the Safety need. Both `live.ps1`
and `run-godot.ps1 -Train` use this set by default. Saved policies keep their
archived package set; explicit legacy experiment configurations remain supported.

The controlled [continual-learning acceptance experiment](docs/continual-learning.md) passed all five fixed training seeds: mean hunger error fell 53.43% versus the frozen policy and 52.22% versus random actions, with thermal error 0.009058 below the 0.05 limit. This result uses the explicit experimental configuration and does not establish balanced behavior in the original default world.

![A trained agent maintaining body temperature beside a smoothly varying heat source](docs/images/thermoregulation.png)

*Actual Godot capture of the trained policy. The creature is the green circle; the orange marker is the heat source.*

## What works today

- One seedable, bounded 100 × 100 world, one circular agent and one central heat source.
- A smoothly changing temperature field and body temperature with thermal inertia.
- Component-based world objects and egocentric direct-geometry vision behind a replaceable backend.
- Bounded hunger, a continuous seeded supply of edible apple entities and automatic feeding at contact, using the shared drive reward.
- Variable observation tokens, trainable type embeddings and a Deep Sets state encoder.
- Shared scalar Q scoring for variable sets of legal action candidates.
- CPU Double DQN with replay, Adam, Huber loss, gradient clipping and a target network.
- Fast headless training, frozen-policy evaluation and verified checkpoint loading.
- A Godot .NET view with a heat field, movement trail, generic homeostasis history and a dynamic needs HUD.

Controllers run locally in C#. Graphical resources are independent from physical modules.

## Quick start: Windows x64

For a graphical launcher with run modes, training-speed presets, configuration and checkpoint pickers, use the **ArtificialLife.Launcher** project in `ArtificialLife.sln` (F5 in Visual Studio). Visual Studio Code users can press F5 and choose **ArtificialLife: лаунчер**. See [the Russian launcher guide](docs/launcher.ru.md).

Clone the repository into a writable directory. Windows PowerShell 5.1 or PowerShell 7 and internet access are sufficient; administrator privileges are not needed.

```powershell
./tools/bootstrap.ps1
./tools/build.ps1
./tools/test.ps1
./tools/train.ps1
./tools/run-godot.ps1
```

If Windows blocks an unsigned script, use a process-only policy override; organization-enforced policies may still apply:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/bootstrap.ps1
```

Bootstrap detects the architecture and always uses the **exact pinned .NET SDK** from `.tools/dotnet10`. If that repository-local SDK is missing or has the wrong version, bootstrap downloads the official SDK ZIP automatically. A compatible Godot .NET executable is reused when available or downloaded into `.tools/`. SHA512 hashes are checked before extraction. NuGet packages also live in `.tools/nuget`. Nothing is installed globally. Bootstrap restores centrally pinned dependencies from the repository-defined NuGet source and executes three neural shape tests to exercise the native Torch backend.

`global.json` requires .NET 10 and rolls forward to an installed feature band so IDEs can use a compatible .NET 10 SDK. Bootstrap installs the pinned SDK 10.0.402 and Runtime 10.0.13 under `.tools/dotnet10`; repository scripts use that exact local copy. If Visual Studio already displayed an SDK-resolution error, reload the solution after bootstrap.

Windows ARM64 and x86 are explicitly rejected: the upstream LibTorch CPU dependency selected for this MVP has Windows x64 binaries only. The script does not pretend that installing an ARM64 Godot editor would solve that limitation.

Downloads are several hundred MB, mostly the SDK and LibTorch. They are ignored by Git. Bootstrap is repeatable and accepts `-Headless` to skip downloading the Godot editor. The .NET SDK is always repository-local; `-ForceLocal` remains accepted for compatibility and only affects Godot reuse. Building the Godot C# project itself requires only its NuGet SDK.

### Pinned environment

| Tool / dependency | Version |
| --- | --- |
| .NET SDK / Runtime / target framework | 10.0.402 / 10.0.13 / net10.0 |
| Godot .NET / Godot.NET.Sdk | 4.7.2 |
| TorchSharp / LibTorch CPU | 0.107.0 / 2.10.0 |
| xUnit v3 / Visual Studio runner | 4.0.1 / 4.0.0 |
| Microsoft.NET.Test.Sdk | 18.10.1 |

Pins are in `global.json`, `tools/versions.json`, the Godot project and `Directory.Packages.props`. The repository-local `NuGet.config` clears machine-wide package sources and restores these dependencies from nuget.org. The solution targets .NET 10 LTS, supported through November 14, 2028; Godot 4.7 supports .NET 8 and later.

Official references: [.NET 10 releases](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json), [Godot 4.7.2](https://github.com/godotengine/godot/releases/tag/4.7.2-stable), [TorchSharp](https://www.nuget.org/packages/TorchSharp/0.107.0), [xUnit v3](https://www.nuget.org/packages/xunit.v3/4.0.1).

### Useful commands

```powershell
./tools/build.ps1 -Headless             # CLI and tests without Godot project
./tools/test.ps1 -Fast                  # omit the short learning experiment
./tools/train.ps1 -Steps 100000
./tools/train.ps1 -Config configs/default.json -Checkpoint artifacts/checkpoints/thermal
./tools/evaluate.ps1                    # reload weights; exploration is disabled
./tools/run-godot.ps1 -Smoke             # start Host, attach Viewer, exercise UI, exit
```

The CLI also works directly with a compatible SDK:

```powershell
dotnet run --project src/ArtificialLife.Cli -c Release -- train --config configs/default.json
dotnet run --project src/ArtificialLife.Cli -c Release -- evaluate --checkpoint artifacts/checkpoints/thermal
```

The project includes conditional Linux x64 LibTorch support. Linux is not part of the verified bootstrap workflow: install SDK 10.0.402, run `dotnet restore` to resolve Linux packages, then build/run the CLI. CI verifies Windows x64.

## World Host and independent Viewer

All CLI modes execute modular RuntimeSession steps. Godot is a separate 2D
WebSocket observer; closing it does not stop a world. See
[architecture, protocol and commands](docs/observation-runtime.md).

```powershell
./tools/world.ps1 run --mode world-only --config configs/world-only.json --directory artifacts/worlds/demo
# In another terminal:
./tools/world.ps1 list
./tools/viewer.ps1 -Session SESSION_ID
./tools/world.ps1 stop --session SESSION_ID
```

Learning: `world run --mode learning --config configs/runtime.json --directory DIR`.
Inference/evaluation: `world run --mode inference --policy CHECKPOINT --directory DIR`
(or `--mode evaluation`). Research retains its existing experiment/campaign commands;
each worker exposes its training and evaluation worlds through the same protocol.
`train.ps1` runs the episodic policy over this same modular runtime.

The convenience `run-godot.ps1 -Train` starts a separate protected Host and Viewer.
The Viewer itself accepts only observation/session/capture options. Its keys are
Space (pause), 1–4 (1x/10x/100x/Max), S (policy/world checkpoint), E (isolated
frozen evaluation), R (world reset), Ctrl+Shift+R (new learning life), F12 (PNG).
Host capabilities determine which commands are permitted.

Policy checkpoints remain inference exports, without optimizer/replay persistence.
World-only checkpoints preserve physical state and RNG. A crashed learning worker
is reported as failed rather than silently restarting its optimizer.

## Measured experiment

The default 100,000-step run is compared with uniform random legal actions and an initialized neural policy. Evaluation uses ε = 0 for both neural policies, five independent initial-state seeds and four 400-step episodes per seed. All 8,000 evaluation steps count, including initial recovery; there is no discarded warm-up.

The measured results and environment are recorded in [the experiment report](docs/experiment.md). A normal full test run also includes a separate 30,000-step learning regression test using different evaluation seeds.

| Policy | Mean absolute error | Comfort time | Mean reward |
| --- | ---: | ---: | ---: |
| Random legal actions | 9.089 °C | 5.39% | -0.5525 |
| Untrained greedy network | 14.747 °C | 0.48% | -0.9369 |
| Trained greedy network | **0.327 °C** | **96.54%** | **+0.9149** |

The recorded training loop took 44.54 seconds on the development PC; evaluation and build time are additional.

## Architecture

Arrows below denote project references. Simulation state reaches Viewer only through WebSocket.

```mermaid
flowchart LR
    CLI --> Hosting
    Hosting --> Application
    Hosting --> Observation.Contracts
    Application --> Core
    Application --> Brain
    Application --> Packages
    Brain --> Core
    Packages --> Core
    Godot[Godot Viewer] --> Observation.Contracts
    Godot --> Packages
    Godot --> Godot.Contracts
    Godot.Contracts --> Core
```

Core and Application contain no Godot references. Viewer has no Application,
Brain or TorchSharp dependency. Another frontend can consume the same versioned
Observation API without implementing a simulator or learning loop.

```text
src/
  ArtificialLife.Core/                   neutral world/module/controller contracts
  ArtificialLife.Brain/                  learning algorithms and policy implementations
  ArtificialLife.Modules.*/              physical mechanics and motivation packages
  ArtificialLife.Application/            common runtime steps, lifecycle policies, checkpoints, experiments
  ArtificialLife.Hosting/                Supervisor/Worker, protected commands, WebSocket and discovery
  ArtificialLife.Observation.Contracts/  JSON messages and independent observation client
  ArtificialLife.Packages/               shared catalogue/import/integrity/assembly loading
  ArtificialLife.Godot.Contracts/        graphics-only presentation interfaces
  ArtificialLife.Godot/                  independent 2D Viewer and visual foundation
  ArtificialLife.Launcher/               World/Viewer launch and local session selection
tests/ArtificialLife.Tests/              regression and real transport tests; numerical legacy reference
configs/                                runtime, lifecycle and research configurations
tools/                                  launch, package and integration checks
docs/                                   architecture, protocol and research documentation
```

One test project keeps this small repository easy to navigate. Test files separate the concerns without multiplying project boilerplate.

## Extensible senses, actions and drives

Each observation has a registered type slot and up to four normalized numeric features. A shared MLP encodes every token; masked mean pooling produces a fixed 32-number state representation. Both observation and action embedding tables have 256 reserved slots.

Actions are candidates containing a type and parameters. The same neural scorer computes `Q(state, candidate) → scalar` for each legal candidate. The nine movement choices share one movement type with different direction parameters. Future capabilities can use unused slots without adding output neurons.

Modules report scaled homeostatic drives and can provide independent scalar signals. A replaceable motivation package determines the learning signal; the shipped Homeostasis policy implements the original weighted-error formula. Temperature supplies raw local samples and a body-temperature drive, without a direction to an ideal position.

Runtime additions require a package manifest and activation request, with no new registration in Application or Godot. Architectural capacity does **not** imply that a policy already understands a new sense or action. New learning conditions start a replay revision and an exploration adaptation period while retaining network/optimizer state. See [runtime modules](docs/runtime-modules.md), [neural architecture](docs/neural-architecture.md) and [thermoregulation](docs/thermoregulation.md).

## Tests and CI

Tests cover legal movement, world bounds, seeded trajectories, thermal equations and changing fields, registries, normalized sensory tokens, reward bounds, permutation invariance, variable observation/action counts, new type slots, managed replay and checkpoint round-trips/corruption. Nine live-session tests additionally cover incremental equivalence, complete reset, counters, bounded snapshots, rolling metrics, saving and frozen evaluation. The learning test requires substantial improvement on separate seeds rather than a decrease in loss.

GitHub Actions restores the centrally pinned packages, builds every C# project including the Godot adapter, and runs tests. Actions are pinned by commit. The workflow does not download or launch the editor; the Godot runtime smoke command is available locally.

`global.json` selects [Microsoft.Testing.Platform for .NET 10](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-with-dotnet-test). Run `dotnet test --project tests/ArtificialLife.Tests -c Release`; select xUnit tests with `--filter-method` or `--filter-trait`. CI writes its TRX report with the built-in xUnit reporter (`--report-xunit-trx`). The repository scripts continue to use the pinned local SDK.

## Limitations and future work

This is a controlled numerical experiment, not a biologically realistic organism. Learning has been validated on this small family of thermal worlds; arbitrary new physics or modules need new evaluation. Observations are fully available local samples, not noisy sensors. There is one CPU agent, fixed feature widths and finite embedding capacities. There is no model migration or exact optimizer/replay resume. The full workflow was also run from a clean source copy with no local SDK, Godot, package cache or build outputs: bootstrap downloaded and verified both tools, all 26 tests passed, retraining reproduced the evaluation metrics and Godot loaded the checkpoint. This was on the development PC, not a pristine Windows VM.

The following are **future milestones, not implemented**:

- General continual learning beyond the measured controlled scenario and migration to different neural architectures.
- Multiple agents and exact restoration of optimizer/replay/controller temporal state.
- LLM director and LLM-generated modules.
- Remote server execution, a web frontend and WebSocket streaming.
- Sharing worlds with other people.

The preserved legacy/default world uses the same Hunger/Food module contracts: one apple every 200 ticks, up to eight at once, at least ten units from the agent. Eating frees capacity; reset clears food and restarts the independent schedule. New research uses the separately validated ecology-v2 supply and threat parameters described above. Existing thermal-only benchmark numbers describe the earlier experiment. Policies saved before Hunger require retraining for the added observation keys.

World Only and protected Codex-driven development are available through
[`tools/world.ps1`](tools/world.ps1). See the [operation and recovery guide](docs/autonomous-world.md)
for complete physical checkpoints, isolated DLL validation, rollback, JSON and PNG.

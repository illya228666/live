# Wild boar runtime package

For step-by-step activation in a running simulation, see the
[Russian activation guide](wildlife-activation.ru.md).

`wildlife@1.0.0` adds a freely wandering wild boar, an instantaneous Safety need,
an external PNG sprite and a package-owned Godot renderer. It uses the existing
runtime API v1. Application, Brain, Homeostasis and the Godot host need no mechanic
registration or implementation references.

Wildlife is included in the standard `tools/package.ps1` build and in
`configs/runtime.json`. New default headless lives and Godot live-training runs
start with one boar and Safety already active. To test adding it later, use a
startup configuration without the wildlife entry (as `wildlife-smoke.ps1` does).
Loading a saved policy retains that policy's archived package set.

## Build without activating

```powershell
./tools/package-wildlife.ps1
```

The ready package is `artifacts/packages/wildlife/1.0.0/` and contains:

- `ArtificialLife.Modules.Wildlife.dll` and its `.deps.json`;
- `ArtificialLife.Modules.Wildlife.Presentation.Godot.dll` and its `.deps.json`;
- `assets/boar.png`, a standalone 256 × 256 transparent sprite;
- `module.json`, including SHA256 hashes of both assemblies, dependencies and PNG.

The build does not activate the package, change startup configurations or rebuild
other published packages. Published version directories cannot be overwritten.
For a new version use `-Version 1.0.1`; for an isolated catalogue use
`-OutputRoot PATH`. Assemble and review new settings before publishing a version;
do not edit published manifests or assets used by a running life.

Only when ready for the later runtime test, submit:

```powershell
./tools/activate.ps1 -Package wildlife@1.0.0
```

The receiving runtime must use the corresponding package catalogue and inbox and
provide `vision.appearance` and `motivation` (the shipped Vision and Homeostasis
packages do). This command is documentation; the build never submits it.

## Settings and behavior

| Setting | Default | Meaning |
| --- | ---: | --- |
| `initialCount` | 1 | Animals created on first installation only; integer ≥ 1 |
| `speedFactor` | 0.75 | Multiplier of configured `World.MovementSpeed`; ≥ 0 |
| `minTurnSeconds` | 3 | Minimum simulated seconds between random targets; > 0 |
| `maxTurnSeconds` | 8 | Maximum interval; ≥ minimum |
| `turnDegreesPerSecond` | 45 | Maximum angular speed; > 0 |
| `dangerRadius` | 10 | Center-to-center danger radius in world units; > 0 |
| `driveWeight` | 1 | Safety weight in Homeostasis; ≥ 0 |
| `minSpawnDistance` | 15 | Minimum distance from agent on installation; ≥ 0 |
| `diameter` | 4 | Vision diameter and rendered sprite canvas size; > 0 |

All numeric settings must be finite. Settings can be supplied in a new package
manifest, or via `RuntimeOptions.Settings["wildlife"]` before its first activation.
The latter does not activate the package. An upgrade follows the host's normal
manifest-settings rules; `initialCount` is not reapplied to an existing population.

Each animal stores `heading`, `targetHeading` (radians, zero points right) and
`nextTurnAt` (absolute simulation time) in schema-1 `wildlife.boar`. Random state
uses the world's independently seeded `wildlife` stream. Turning integrates
constant-speed arcs; mirrored wall crossings preserve travel, including overshoot
across several walls. The animal ignores the agent, food and heat, does no damage,
blocks no actions, and never respawns automatically.

Initial placement tries 256 random points, then the most distant map corner.
Impossible placement rejects activation transactionally. Re-activation and
upgrades preserve animal identities, positions, turn state and random state.

Safety danger is `max(0, 1 - distance / dangerRadius)`, taking the maximum across
animals, or zero for an empty population. There is no fear accumulation. The
`safety` drive has target 0, scale 1 and configurable weight. The existing reward
formula combines it with other needs: proximity lowers reward, but reward need
not become negative. Adding this need also changes the weighted average when safe.

Vision uses `vision.appearance.boar.v1` with the existing four geometric features;
it does not inspect sprite pixels. `safety.threat.v1` adds one scalar danger feature.
The package adds no actions. Activation creates a learning revision through the
existing host, preserving weights/optimizer and isolating old replay revisions.

## Sprite and renderer

The source assets live under `assets/wildlife/`: `boar-source.png` is the original
built-in imagegen output; `boar.png` is the processed transparent runtime asset.
`prompt-used.txt` records the exact generation prompt and `pipeline-meta.json`
records deterministic generate2dsprite processing and QC. The source image remains
unchanged. The sprite is a single overhead pose with brown bristles and ivory
tusks, facing right. It rotates with the animal's heading; it is not animated.

The separate renderer loads `assets/boar.png` from its version-specific package
directory with `Image.Load` and creates an owned `ImageTexture`. No editor import
or `res://` asset is required. It draws a translucent danger circle at the actual
configured radius and restores the canvas transform after rotating the sprite.
The presentation scope releases the texture when its assembly generation retires.
Headless simulation never loads the renderer or Godot assemblies.

## Isolated verification

```powershell
./tools/wildlife-smoke.ps1              # hidden separate Godot process, PNG capture
./tools/wildlife-smoke.ps1 -Headless    # no graphical capture
```

The script copies base packages into a fresh `artifacts/wildlife-smoke-<id>/`
catalogue, packages wildlife there, and uses a private inbox and checkpoint.
At age 1200 it hot-installs wildlife; at 1600 it replaces its presentation
generation without changing learning conditions. The unchanged Godot host checks
continuous age/training/replay and collects retired simulation/presentation
contexts. The graphical run saves `wildlife.png`, showing the sprite, danger ring
and the generic Safety HUD. Neither mode posts to the live inbox.

`WildlifeTests` loads real compiled DLLs in collectible contexts. It covers seeded
and copied trajectories, smooth turns, speed and boundaries, proximity values,
multiple animals, spawn fallback, invalid settings, sustained reward, installation
continuity, idempotence/upgrades, corrupt packages, inference archives and unload.
Its fixtures use private temporary catalogues and leave the shared packages alone.
Tests build the new projects as build-only dependencies, with no runtime assembly
reference to either implementation.

These checks establish technical extensibility. Learning to avoid the boar and
retaining other behavior require a separate controlled learning experiment.

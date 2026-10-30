# Autonomous World Only operation

The protected host uses the existing `RuntimeSession`, DLL packages, component
state, activation transactions and immutable checkpoint generations. The CLI
supervisor owns one simulation worker. Test workers use disposable copies, and
Godot observation mode only renders a copied frame.

## Prepare once

```powershell
./tools/build.ps1
./tools/package.ps1 -Version 1.1.0 -OutputRoot artifacts/world-packages
./tools/world.ps1 run --directory artifacts/worlds/main --config configs/world-only.json
```

Package preparation is explicit. Launch commands no longer rebuild the catalogue.
`package.ps1` still supports the existing development 1.0.0 workflow; do not run
that workflow against versions used by a live process. New published versions
must have a new identity. `-OutputRoot` supports an external staging directory.
Reusing a non-development output version is rejected.

Subsequent `world run --directory ...` restores the directory's recorded physical
state; a supplied config does not replace an existing world's configuration.
Use a new directory to start a new world. The CLI requires WorldOnly for protected
operation. Existing live/training/evaluation commands retain their controller paths.

## Control from another process

```powershell
./tools/world.ps1 status --directory artifacts/worlds/main
./tools/world.ps1 snapshot --directory artifacts/worlds/main
./tools/world.ps1 checkpoint --directory artifacts/worlds/main --id before-change
./tools/world.ps1 update --directory artifacts/worlds/main --sources C:/build/new-mechanic --id install-001
./tools/world.ps1 screenshot --directory artifacts/worlds/main --id capture-001
./tools/world.ps1 rollback --directory artifacts/worlds/main --id rollback-001
./tools/world.ps1 stop --directory artifacts/worlds/main --id stop-001
```

Quote `--sources "PATH1;PATH2"` for a bundle. Inputs are package folders,
`module.json` files or ZIPs accepted by `RuntimePackageImporter`; a standalone DLL
is not a complete package. A module can be compiled against Core in an external
build folder. Its manifest declares hashes and `supportsWorldOnly: true`.

Control commands return the result file path immediately. Read
`commands/<id>.result.json` to wait for completion; requests/results use the
existing temporary-file and atomic `.ready.json`/`.result.json` convention.
Status/snapshot return the latest complete observation immediately. Reuse an ID
only to retrieve the same operation's result. An accepted update means activation
succeeded; `recovery.json.Pending` and `version-working` journal events distinguish
probation from completion. A later failure is recorded as recovery and quarantine.

Legacy `activate.ps1` works with `-Inbox artifacts/worlds/main/activation` and exact
versions already imported into `artifacts/worlds/main/packages`. It is routed
through validation rather than directly into live activation.

## Save and recovery guarantees

`WorldCheckpoint` stores physical state, exact clocks and allocator, all entity
components/module state/RNG streams, ordered registries, revision, effective
settings and archived checksummed packages. Restore registers callbacks without
running migrations or component initializers. Missing state and bad hashes fail
closed. `current` selects a fully written immutable generation; incomplete files
are ignored. Explicit generation paths also verify their checksum.

World checkpoints can contain an inhabited world, but reopening one requires an
explicit controller. Optimizer, replay and controller temporal/RNG state are not
restored by this API. Existing `RuntimeCheckpoint` policy exports remain separate.

Before publication the worker saves a checkpoint and tests the candidate in a
separate process: registration/migration, 1,000 physical steps, checkpoint roundtrip,
continuation equivalence, and Godot rendering when presentation packages exist.
File checks are repeated during import and activation. The main world keeps stepping
while validation runs. A fresh checkpoint and durable update intent precede live
activation; overlapping updates are rejected during validation/probation.

Defaults: observation/heartbeat every second, periodic checkpoint every 30 seconds,
validation timeout 60 seconds, worker heartbeat timeout 60 seconds, probation 1,000
steps. Configure `--checkpoint-seconds`, `--validation-seconds`, `--heartbeat-seconds`
and `--probation-steps` on `world run`. Budgets must be positive. Long checkpoints
can require a larger heartbeat timeout.

A failed provisional deployment restores its pre-activation checkpoint and
quarantines that exact candidate fingerprint. Ordinary worker crashes restore the
latest checkpoint; a repeated failure falls back to the previous deployment, then
stops if recovery also fails. An interrupted update intent is rolled back on host
startup. Manual rollback restarts from the previous deployment; active module
removal and reverse migrations are unnecessary. State after the selected checkpoint
is lost. This is process recovery, not zero-downtime execution or a sandbox for
hostile native code. Stable semantic bugs require observation and explicit rollback.

Three recent periodic generations and deployment rollback points are retained.
The JSONL journal rotates at 10 MB with four older files. Validation folders retain
reports, candidate packages and screenshots for investigation; archive/remove old
validation folders only while no validation is active. Published package versions
are retained so references are never silently broken.

## Observation and authoring

`observation.json` includes the complete frame, package versions/hashes, operation,
fault, checkpoints, throughput, step/system timings and module metrics.
`events.jsonl` records checks, publication/activation, commands, saves and failures.
Fault observations are diagnostic and must never be used as restore checkpoints.
PNG captures have a JSON sidecar recording the exact age/revision and source frame.
Rendering requires the pinned Godot installation and a built Debug Godot project.
Missing or failing renderers reject the capture/update without stopping physics.

Render an exported observation directly without creating a simulation:

```powershell
# Use the pinned Godot executable discovered by tools/common.ps1:
. ./tools/common.ps1
& (Get-Godot) --path src/ArtificialLife.Godot -- --observe C:/world/observation.json --capture C:/world/frame.png
```

World-only modules must check `world.HasAgent` before accessing `world.Agent`.
There is no agent entity in `AllEntities` in World Only. Components, schedules,
counters and every random stream belong in the existing value state; do not keep
mutable persistent state in plugin instances or use untracked `System.Random`.
The unchanged timestep and system ordering govern autonomous physics.

Register optional diagnostics with
`builder.Metrics.Add(frame => [KeyValuePair.Create("pressure", value)])`.
Providers receive a copied immutable frame; finite values are namespaced by module.
Exceptions are journaled and do not fault physical advancement. Providers must be
quick and side-effect free. No migration is required merely to add diagnostics.

## Verification

```powershell
./tools/world-smoke.ps1
./tools/test.ps1
./tools/runtime-smoke.ps1
```

World smoke validates corruption rejection, isolated hangs, duplicate ownership,
late exceptions and hangs, watchdog rollback, process death recovery, manual rollback,
subsequent corrected updates, request idempotency, JSON/PNG and at least 100,000
steps. Tests also verify deterministic restoration including future IDs and RNG.

The October 30 implementation run completed 1,602,349 world steps in the extended
smoke. A full 209-test run passed 205 tests, including learning. Four registered
scientific-review checks intentionally reject the changed Core/runtime fingerprint.
Their archived evidence was preserved, and the runtime gate was not weakened.
The final focused runtime/import/world suite passed all 43 tests, the solution
built without warnings, and Godot RL activation/unload smoke passed. Separate regression tests replay the three preserved held-out ecology trajectories
and match both action and physical-state hashes. Before future scientific
comparisons, rerun the baseline suitability audits and register fresh evidence;
these infrastructure tests do not re-admit the old scientific reviews.

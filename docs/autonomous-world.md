# Autonomous World Host

The shared Host supervises World Only, Learning, Inference and Evaluation workers.
All modes use the Application session factory/execution pipeline. Godot observes
over WebSocket as a separate process. See [protocol and modes](observation-runtime.md).

## Start and connect

```powershell
./tools/build.ps1
./tools/world.ps1 run --mode world-only --directory artifacts/worlds/main --config configs/world-only.json
./tools/world.ps1 list
./tools/viewer.ps1 -Session SESSION_ID
./tools/world.ps1 pause --session SESSION_ID
./tools/world.ps1 resume --session SESSION_ID
./tools/world.ps1 screenshot --session SESSION_ID --target artifacts/frame.png
./tools/world.ps1 stop --session SESSION_ID
```

Prepare packages explicitly before launching. Published versions are immutable;
use a new version and a separate staging catalogue for updates. Packaging preserves
an existing 1.0.0 baseline and rejects overwriting other published versions.
Each independent world uses its own directory. Closing Viewer/Launcher leaves it
running. Headless execution defaults to Max; TimeStep is independent of pacing.

## Protected commands

```powershell
./tools/world.ps1 checkpoint --directory artifacts/worlds/main --id before-change
./tools/world.ps1 update --directory artifacts/worlds/main --sources C:/build/new-mechanic --id install-001
./tools/world.ps1 rollback --directory artifacts/worlds/main --id rollback-001
./tools/activate.ps1 -Session SESSION_ID -Sources C:/build/new-mechanic
```

File commands return `commands/<id>.result.json`; read it for completion. Reuse an
ID only to retrieve that operation's result. WebSocket commands share the protected
handler; asynchronous updates first acknowledge queue admission. Updates accept
package folders, manifests and ZIPs, with exact logic/hash/path checks. An active
probation is distinguished from committed deployment by recovery.json and events.
Research/Evaluation reject external mutation. Exact rollback is World Only.

Validation runs registration/migration, physical steps, checkpoint roundtrip and
continuation checks in an isolated process. It does not load Godot. Missing or
faulty graphics use Viewer fallback and cannot reject physical activation.
The live world keeps stepping during validation; durable checkpoint/update intent
precedes activation. Validation/probation reject overlapping updates.

## Persistence and failures

World checkpoint v1 retains world components, module state, RNG, allocator, clocks,
registries, effective settings and archived packages. Restore registers callbacks
against saved values without rerunning initializers/migrations. Immutable
generations, checksums and an atomic pointer reject incomplete/corrupt state.

World Only restores automatically after worker failure. Failed provisional
deployments roll back and quarantine their candidate fingerprint; repeated failure
can fall back to the previous deployment. Recovery loses steps after its checkpoint.
Inhabited physical checkpoints require an explicit controller. Policy v1–v3 exports
remain readable but omit optimizer/replay. Learning state loss terminates the run;
a saved policy can begin a new training run in a new directory, not exact continuation.

Default checkpoint interval is 30 seconds; watchdog/validation timeouts are 60
seconds; probation is 1,000 steps. Use the corresponding world run options to tune
them. The owner services commands and heartbeat while paused. Long checkpoints
may need a larger heartbeat timeout. Journals rotate, checkpoint retention is
bounded, and deployment/archive package versions remain immutable.

## Observation and modules

`observation.json` remains a local diagnostic export. Live Viewer and session CLI
use WebSocket full states, capabilities, metrics and explicit stream generations.
Capture launches Viewer over the same protocol; its JSON sidecar identifies the
actual displayed age, revision, session, epoch and sequence. Camera, selection,
interpolation and graphical caches belong exclusively to Viewer.

World-only modules must check world.HasAgent before accessing the agent. Persist
components, schedules and every RNG stream in host-owned value state. Optional
metrics receive copied frames, must be side-effect free, and must not alter physics.
Module authoring is described in [runtime-modules.md](runtime-modules.md).

Run world-smoke.ps1 for validation/watchdog/rollback/checkpoint recovery,
observation-smoke.ps1 for live Godot connections, and visual-smoke.ps1 for a new
declarative PCK module, graphical updates and failure fallback. Current measured
results are in [the implementation report](runtime-unification-results.md).
Archived research evidence is preserved; changed fingerprints require new
suitability evidence before future scientific comparisons.

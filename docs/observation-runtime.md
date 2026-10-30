# World execution and observation

Production execution uses `RuntimeSession.Step`: optional controller action,
physical systems, motivation, optional learning transition, and sampled observation.
World Only has no agent/controller. Inference/evaluation call the same step with
learning disabled. Episodic training supplies terminal before replacing the world;
continuous learning never inserts episode-boundary resets.
`RuntimeSessionFactory` is the common composition boundary for new sessions and
policy/world format adapters. RuntimeEvaluator uses the configured fixed seeds
and episodes with isolated frozen/random controllers; it does not advance the
source learner or consume its RNG.

`Core` owns runtime/physics contracts. `Application` owns execution, controller
composition and experiments. `Packages` owns catalogue/import/integrity/load-context
operations. `Hosting` owns Supervisor/Worker, protected activation, discovery and
WebSocket. `Observation.Contracts` is shared by the host and independent client.
Godot references packages/contracts, not Application, Brain or TorchSharp.
The old orchestration retained under `tests/LegacyReference` is a test reference,
not a production launch path.

## Launch

Build the solution and the Godot Debug project before launch. Prepare packages
explicitly. Run commands from the repository root; independent worlds require
different directories.

```powershell
./tools/world.ps1 run --mode world-only --config configs/world-only.json --directory artifacts/worlds/a
./tools/world.ps1 run --mode learning --config configs/runtime.json --directory artifacts/worlds/b
./tools/world.ps1 run --mode inference --policy POLICY_DIRECTORY --directory artifacts/worlds/c
./tools/world.ps1 run --mode evaluation --policy POLICY_DIRECTORY --steps 4000 --directory artifacts/worlds/d
./tools/world.ps1 list
./tools/viewer.ps1 -Session SESSION_ID
./tools/world.ps1 pause --session SESSION_ID
./tools/world.ps1 speed --session SESSION_ID --value 100x
./tools/world.ps1 resume --session SESSION_ID
./tools/world.ps1 checkpoint --session SESSION_ID
./tools/world.ps1 stop --session SESSION_ID
```

Protected update/rollback also retain their file command interface:

```powershell
./tools/world.ps1 update --directory WORLD_DIRECTORY --sources PACKAGE_FOLDER_OR_ZIP
./tools/world.ps1 rollback --directory WORLD_DIRECTORY
```

`live.ps1` starts a protected learning Host. `train.ps1` runs bounded episodic
training using the modular runtime. `evaluate.ps1` loads an inference export.
`run-godot.ps1 -Train` is a convenience wrapper starting separate Host/Viewer
processes. Closing Viewer or Launcher leaves Host running. `world stop` is explicit.
Use `viewer.ps1 -Capture PATH.png` to capture a received world.

Headless execution defaults to Max. `--speed 1x|10x|100x|Max` sets initial pacing.
Learning uses a base 60 steps/s; World Only and Inference use `1 / TimeStep`.
Pacing, physical TimeStep, publication frequency and rendering FPS are independent.

## Protocol v1

The loopback Kestrel server binds an automatically chosen port. A per-process
record in `%LOCALAPPDATA%/Live/sessions` contains endpoint, process id/start time and
heartbeat. Clients reject dead/stale records and refresh the session list. A
protected world's persisted session id survives Worker restart; stream epoch changes.

- `GET /sessions`: active/suspended sessions with run/session/world/epoch ids,
  mode, status, endpoint, explicit parent session id and optional research trial id.
- `WS /observe/{sessionId}?version=1`: versioned JSON messages. Unsupported versions
  fail the handshake. Every published frame contains complete state; no delta codec.
- `GET /resources/{sessionId}/{package}/{version}/{path}`: declared graphical files.
  Clients verify SHA256 and cache resources separately from physical modules.

Messages carry `version`, `kind`, `session`, `sequence`, `metadataRevision`, `state`
and optional `event`. State carries the neutral RuntimeFrame, packages/visual files,
optional controller status, keyed metrics, capabilities, speed, paused and fault.
JSON agent is null for World Only. Speed 0 denotes Max.

Commands contain `id`, `epoch`, `operation`, and optional string `value`.
Results contain `id`, `accepted`, and `message`. Protected asynchronous updates
acknowledge queue admission; completion remains in the existing command results
and protected event log. Old-epoch requests are rejected. Clients never replay
mutating requests automatically after reconnect.

The simulation owner copies state at most 20 times/s on demand. Serialization is
lazy, shared between clients, and executes outside the owner thread. Intermediate
frames are replaceable; structural/lifecycle notifications use bounded client
queues. Overflow forces resynchronization instead of blocking simulation. Full
state restores the presentation after reconnect. A suspended parent retains an
immutable frame so late observers can inspect it while child evaluations execute.

Evaluation/Research forbid external intervention. Interactive capabilities include
pause/resume, speed, checkpoint, world reset or new learning life, and isolated
frozen evaluation. Protected Hosts additionally accept update/stop and, for World
Only, rollback. The Host checks permissions; UI availability is only a convenience.

## Visual packages

Physical activation no longer calls a graphics preparation hook. Logic files retain
mandatory identity/hash/path checks. Missing/corrupt graphics cause Viewer fallback.
Existing `IGodotPresentation` implementations work from copied RuntimeFrames.

A manifest can instead supply `visuals`:

```json
{
  "visuals": [{
    "key": "creature",
    "component": "example.creature",
    "sprite": "assets/creature.png",
    "scale": 0.1,
    "layer": 75,
    "pulse": false,
    "bindings": [{
      "property": "rotation", "component": "example.creature", "field": "heading"
    }]
  }]
}
```

Declare loose files/PCKs in `presentationFiles` with SHA256. Scene entries use
`scene` and a `res://` path. Pack internal paths must be namespaced by module/version.
Bindings support rotation, scale, alpha, visible and sprite frame with numeric
scale/offset; they do not evaluate code. No scene/sprite produces a colored primitive.
The packaging script supplies declarative agent, food and wildlife descriptions;
the temperature field remains programmable.

PCK loading uses native Godot resource packs. C# presentations use the existing
scoped loader. Arbitrary C# scene generations may require restarting Viewer;
World remains independent. Use separate published versions, never overwrite active
package files. Camera, selection and inspector are Viewer-only. Interpolation
resets on epoch changes and skips large jumps or `visual.teleport` components.
Declarative objects include short appearance/disappearance fades and optional
pulse/scene/sprite animation. Package a foundation example with
`./tools/pack-visuals.ps1 -Output artifacts/foundation.pck`; the default internal
namespace is `res://live-foundation/1/`. Custom packs require module/version paths.
PCKs contain resources; C# assemblies are separate, following the
[Godot pack documentation](https://docs.godotengine.org/en/stable/tutorials/export/exporting_pcks.html).

## Persistence and verification

World-only checkpoints retain physical state, RNG and archived packages. Policy
formats v1–v3 remain readable. Policy exports do not contain optimizer/replay state.
Learning crash is a failure, not an automatic exact continuation. Use a new directory
and `--policy` for an explicit warm start. Protected live activation preserves the
in-memory learner; exact controller-state rollback is not advertised.

`tools/observation-smoke.ps1` checks real Worker/Godot late join, pause heartbeat,
resume, process recovery, reconnect and concurrent World Only/Learning sessions.
`tools/world-smoke.ps1` checks isolated validation, late module failures/hangs,
watchdog, activation, rollback, recovery, idempotency and graphical capture.
Observation tests use real Kestrel/WebSocket connections and compare learning
world/action/weight hashes with fast and unread clients. Archived scientific
reviews are not automatically reapproved after runtime/code identity changes.
`tools/visual-smoke.ps1` publishes a new fixture module, updates a connected Viewer
through versioned PCK routes, checks unchanged training conditions and graphical
failure fallback, and saves a real rendered capture. Captures have a JSON sidecar
identifying the actual displayed frame rather than a prior file export.
`tools/observation-benchmark.ps1` runs three 30,000-step repetitions with no Viewer,
a real Viewer and an unread socket; it compares weight hashes, median throughput,
publication demand and Worker memory. One sequential sender per connection follows
the [.NET WebSocket contract](https://learn.microsoft.com/en-us/dotnet/api/system.net.websockets.websocket.sendasync?view=net-10.0).

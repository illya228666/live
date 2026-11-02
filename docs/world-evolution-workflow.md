# World Evolution Workflow

PowerShell 7, repository root, pinned SDK. These tools compose the existing package
importer, protected Supervisor/Worker and WebSocket API, not a new agent/platform.

## Author and publish

Create a normal module project implementing `IRuntimeModule` and referencing Core.
Put `package.json` next to its project; the runtime's generated `module.json`
continues to use the existing `ModuleManifest` contract without a new version:

```json
{
  "project": "MyModule.csproj",
  "testClasses": ["My.Tests.ModuleTests"],
  "packages": [{
    "manifest": {
      "id": "my-module", "version": "1.0.0", "apiVersion": 1,
      "entryType": "MyModule.RuntimeModule", "supportsWorldOnly": true,
      "dependencies": [], "provides": ["my-capability"], "requires": [],
      "schemas": {"my.component": 1}, "stateSchemaVersion": 1, "settings": {},
      "visuals": []
    },
    "files": {"assets/sprite.png": "assets/sprite.png"},
    "visualPack": {"source": "visuals", "file": "visuals.pck"}
  }]
}
```

`files`, `visualPack`, `testClasses` are optional; paths are relative to the
description. Multiple package entries expose independent entrypoints from one
assembly. Component/state schemas, settings, exact dependencies, capabilities and
visuals pass through. `@id@`/`@version@` in scene paths expand for namespaced PCKs.
The existing `pack-visuals.ps1` builds PCKs. Loose sprites/scenes and programmable
presentation DLLs go in `files`; the normal presentationAssembly/type/order fields
retain their meanings. Logic DLL/deps and presentation hashes are generated.

```powershell
./tools/package-module.ps1 -Description src/MyModule/package.json -Version 1.1.0
./tools/package-module.ps1 -Description src/MyModule/package.json -Id my-module -Version 1.1.1 -NoBuild
```

Publication uses `RuntimePackageImporter`/`PackageCatalog`, not another registry.
Published versions are never overwritten. PCK regeneration need not be byte-for-byte
identical: reuse published `-Sources`, don't regenerate an old version. The Garden
script is now a thin compatibility wrapper; foundation/scientific scripts remain
available and established baseline publications are not rewritten.

## Check, deploy, observe

```powershell
./tools/evolve.ps1 -Action check -Description src/MyModule/package.json -PackageId my-module -Version 1.1.0 -Checkpoint SAVED_WORLD -Output artifacts/iterations/check-01
./tools/evolve.ps1 -Action check -Sources artifacts/packages/my-module/1.1.0 -NoBuild -NoTests -Checkpoint SAVED_WORLD -Output artifacts/iterations/recheck-01
./tools/evolve.ps1 -Action deploy -Directory WORLD_DIRECTORY -Session SESSION_ID -Sources artifacts/packages/my-module/1.1.0 -Output artifacts/iterations/deploy-01
./tools/evolve.ps1 -Action observe -Directory WORLD_DIRECTORY -Capture -Save -Output artifacts/iterations/observe-01
```

Check builds only the described project, then the test project without rebuilding
its references; classes come from `testClasses` or `-TestClass`. `-TestProject`
selects another project. `-NoBuild` reuses binaries; `-NoTests` explicitly omits
tests. An optional checkpoint invokes the existing isolated 1000-step/restore/
continuation validator. A published source needs no module build.

Deploy verifies packages, submits through `activate.ps1`, waits for the protected
result AND probation (`Pending=null`), confirms active versions, checks faults,
progress and events, captures a real Godot frame and confirms a saved checkpoint.
Admission alone is not success. Resume a paused world explicitly before deployment.

Output is compact JSON plus `result.json`, per-stage logs, snapshots, metric deltas,
TRX, PNG/sidecar and saves under `-Output`. Errors distinguish build, package, test,
validation, activation, probation and observation. Repeat the same request/output
after a partial failure; completed runs return their summary without repeating
side effects. Partial checks reuse their published package and already passed tests;
failed isolated validation gets a new attempt directory. Rejected candidates need a corrected version/new output and operation
ID. `-Id` pins an operation. Observe without `-Capture`/`-Save` performs neither;
no iteration stage automatically restarts Viewer.

## Pinned Host

```powershell
./tools/host.ps1 -Action publish
./tools/host.ps1 -Action start -Directory WORLD_DIRECTORY -Config configs/world-only.json
./tools/host.ps1 -Action start -Directory NEW_DIRECTORY -Restore SAVED_WORLD
./tools/world.ps1 run --directory WORLD_DIRECTORY
./tools/host.ps1 -Action switch -Directory WORLD_DIRECTORY -Release artifacts/hosts/NEW_RELEASE
```

Publish builds CLI once and pins its complete output under immutable
`artifacts/hosts/<release>/`; `-NoBuild` deliberately reuses compiled output.
`host.json` contains hashes/revision, `current.json` chooses the next default,
and the World's `host-release.json` records its actual executable/PID. Worker and
validation children use that same executable. Dev/module builds cannot overwrite it.
Starting an existing directory without `-Release` reuses its recorded Host.
All long-lived launch paths verify the release manifest and file hashes. A legacy
record pointing to dev `bin` is rejected: explicitly select a published release
with `host.ps1 start -Release ...` or `world.ps1 run -HostRelease ...` for a stopped
World, or use the controlled switch for an active World Only session.
`world.ps1 run`, `live.ps1`, `run-godot.ps1` use pinned releases. Control commands can
use the dev CLI without replacing Running Host.

Switch is deliberately World Only: refuse pending operations, pause, save a
consistent checkpoint, verify the new Host on its isolated copy, stop gracefully,
restore the same session/directory and check continuity. Failed startup restores
the saved recovery pointer and previous Host. Learning is refused: optimizer/replay
are not persisted. Keep old releases for rollback; never delete an active release.

## Discover, recover, prove life

`world.ps1 list` discovers sessions. Restart a stopped world from its **same**
directory. `host.ps1 start -Restore` into a new directory makes an isolated copy.
Two fresh observations must show the same session, increasing age, expected
revision and no fault; inspect events for recovery, not merely the last frame.
CLI snapshots wait beyond the cached reconnect frame for a fresh publication.

`check-evolution.ps1 -Checkpoint ... -Output NEW_DIRECTORY` accepts a checkpoint
pointer or direct generation. `viewer.ps1 -Session ...` attaches independently;
capture sidecars identify actual session/epoch/age/revision. Disposable E2E:
`evolve-smoke.ps1 -Checkpoint SAVED_WORLD [-NoBuild]` (source world untouched).
Details: [module contracts](runtime-modules.md), [host/recovery](autonomous-world.md),
[observation/visual contracts](observation-runtime.md).

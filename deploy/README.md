# MyPocketWorld operations

The deployment uses Proxmox LXC 106 (Debian 12, 192.168.178.206) and the
Window-to-Europe Cloudflare Tunnel on CT 101. The public origin is TCP 8080.
The server accepts origin traffic from the tunnel's LAN address and container
loopback. The tunnel serves the live viewer at /, status JSON at /api/world
and the public rendering snapshot at /api/scene. Its only POST route is
/hooks/github, which validates the signed release notification.

The first deployment starts World Only with the base set in modules.json.
The installation script expects an administrative webhook secret at
/etc/live/webhook.secret and LIVE_REPOSITORY_ID in /etc/live/status.env.
Keep the secret in the guest and the GitHub webhook configuration. Provision
Debian with Python 3.11+, a GitHub CLI supporting attestations, and the .NET native
dependencies (ICU, OpenSSL, libgomp and zlib). Run sh deploy/install.sh from a
checkout or an archive containing deploy and configs/world-only.json.

## Publish a module

Add its package ID and package description to deploy/modules.json and merge
the module change into master. For example, for the first flora release specify
{"id":"flora","description":"src/ArtificialLife.Modules.Garden/package.json"}
in the modules array. The builder uses the package's declared test classes
and keeps prior versions of unchanged modules. Foundation packages ship as a
group because food has exact-version dependencies on hunger and vision. The
deployed catalogue includes portable visuals declared in `visuals` and `files`.
Use a loose PNG or WebP sprite and declare it in the package files. Add a
`browser` block to the visual for its legend label, world-unit size, optional
slot, and primitive. A Godot scene can declare a loose sprite alongside its
scene path. Godot and PCK generation are outside the server build.

```json
"browser": {"label": "Растение", "slot": "body", "size": 3.8}
```

The matching visual with the highest layer wins within a slot on each
entity. Use the same slot to replace a base appearance and omit it for
decorations that should stack. A slot also honors an explicitly hidden
winner. Visuals can use rotation, scale, alpha, visibility and size bindings.
`disc` and `glow` are built-in primitives. `only: true` marks a
browser-only visual. A missing raster falls back to its declared primitive.
`/api/scene` version 2 exposes resolved appearances and labels.

The Live CI/CD workflow runs the Windows suite and Linux package/scene
checks in parallel. The publish job takes the artifacts from that same run
once both jobs have passed.

The release manifest describes a cumulative desired set of modules. Each
package references its original immutable release asset. The published
manifest has a GitHub build attestation and records SHA-256 for each asset.
The server verifies provenance from the master release workflow, checks the
asset digest, and invokes the existing package importer and world activation
pipeline. An accepted command is provisional until probation has completed.

## Operate

- systemctl status live-world live-status live-reconcile.timer live-reconcile.path
- journalctl -u live-world -u live-reconcile
- Read /var/lib/live/reconcile.json and /var/lib/live/world/events.jsonl for the
  latest delivery outcome and the world's activation history.
- systemctl start live-reconcile.service performs the same check as an incoming
  webhook and the ten-minute timer.
- Use `python3 /opt/live/deploy/live_deploy.py stage-host --tag live-N` to
  verify and install a candidate. It includes the browser files.
- Pin the Host release in /opt/live/host/current. For a Host change, record the
  active session and revision, stop the simulation gracefully, preserve the
  current checkpoint, test it in an isolated copy under the candidate Host, then
  switch the pin and start the same world directory. Keep the previous Host for
  recovery.
- For the first scene v2 rollout, pause reconciliation until the new Host is
  running. The old Host can read the legacy manifest fields while checkpointing
  the newer package may lose the new browser fields. Re-enable the timer and
  webhook path after the switch, then reconcile to apply the visual packages.
- Restore backups into a separate guest with its network disconnected while
  checking the snapshot and active packages. Reattach the public route only
  after deciding which instance is authoritative.

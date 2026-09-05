# Production operations

This release targets one trusted administrator on a private host. Do not forward
the panel port from a router. Stable publication requires the acceptance evidence
in `PRODUCTION-READINESS.md`; configuration alone does not establish readiness.

## Private HTTPS

Use `Caddyfile.private` with an internal DNS name and Caddy's internal CA. Install
the CA root on the administrator's devices. Add the adapted values from
`production.env.example` to `/etc/mcpanel/mcpanel.env`. Keep the existing credentials
and data paths. Restart the panel after changing environment settings.

Only explicitly listed proxy addresses may supply forwarded client addresses or
HTTPS scheme. Caddy connects to loopback; port 6050 remains bound to `0.0.0.0`, but
`RequireHttps=true` rejects direct HTTP API/UI requests. Health probes remain
available over HTTP. Secure, HttpOnly, SameSite cookies are required in this mode.
Use an allowed host matching the private DNS name. Restrict proxy ingress to the
private LAN/VPN in the host firewall. These settings follow Microsoft's
[proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

Check `/health/live` for process liveness and `/health/ready` for database schema
and runtime compatibility. The readiness response reports panel and runtime
versions separately. Minecraft readiness requires startup confirmation; time
alive alone is insufficient.

## Panel backups and remote copies

Open **Panel settings > Backups** for two separate exports:

- **Panel backups** contain panel settings, the administrator account, shared icons,
  encryption keys, Java runtime registrations, and administrative history. They exclude
  instance files, instance registrations, player records, schedules, and console logs.
- **Instance exports** contain all instances or a selected set in one ZIP, including
  current worlds, server files, mods, launch settings, player records, schedules,
  and selected Gate proxies with their keys. Managed Gate backend relationships
  are included only when both instances are selected. External backends are retained.
  Panel accounts and settings, old backups, and Java installations are excluded.

New panel archives use the `panel-settings` manifest kind. Older `panel` archives
still restore with their included instances and are labeled as legacy backups in
the UI. Existing archives are preserved. New instance collections use `instances`;
older single-instance `server` exports remain supported.

The installer gives the panel read access to `mcpanel.env` with root ownership,
the `mcpanel` group, and mode `0640`.

Remote copies are optional. "Off-host" means storing a copy on another machine
so it survives loss of this host. Without a configured destination, no remote
backup overdue incident is raised.

Mount storage from a different machine at `Panel__ReplicationDirectory` using
NFS or SMB. The directory must already exist, be writable by `mcpanel`, and be
reported as a network filesystem. A local directory, bind mount, or another local
disk is rejected. Protect the destination with access controls, encrypted disks,
and a private VPN or encrypted transport. Panel backups contain credentials and
Data Protection keys; instance exports can include proxy keys and plugin
credentials. Keep both private.

Use a systemd mount with explicit dependencies on `mcpanel.service`. Ensure a
failed mount cannot silently fall back to a local directory. The panel performs
one capture when overdue, every 30 minutes by default. It copies to a partial
file, flushes it, reads it back, checks SHA-256, and then publishes its final name.
The Backups tab shows saved bundles and remote-copy verification. Activity shows
backup jobs and failures. When a remote destination is configured, no verified
copy from the last hour creates an incident. Automatic remote replication covers
panel backups only; export and store instance data separately.

Local retention uses age, count, and byte limits, retaining the latest verified
copy and explicitly pinned backups. Restore pins its selected source and safety
copy; unpin these after verifying recovery. Pinned copies may exceed the budget.
Remote retention is controlled at the destination and must preserve a verified
copy. Server exports are deliberate downloads and remain under `data/exports`;
remove old exports during planned maintenance after preserving required copies.

Configure an HTTPS webhook URL in the root-managed file named by
`Panel__AlertWebhookFile`, readable by the service account. Alerts include a stable
incident ID, status, server ID, message, and timestamp. Delivery is retried and
tracked; recipients should deduplicate on incident ID plus status. Resolved
incidents produce recovery notifications.

## Restore onto a clean host

1. Obtain a compatible immutable release, the off-host ZIP, and its independently
   recorded SHA-256. Verify the downloaded ZIP against that digest. Install Java
   versions required by the servers. Keep the panel stopped during recovery.
2. Run the release binary with `--mcpanel-restore-bundle BUNDLE NEW_DATA NEW_CONFIG`.
   Both destination paths must be absent. Extraction checks version, file hashes,
   sizes, paths, and available space before activating the result. Existing data
   is never overwritten. SQLite snapshots are created with its backup API.
3. For new panel-only backups, import an instance export into the restored panel
   while it is stopped:

   ```sh
   MCPANEL_DATA_DIR=/path/to/restored-data MCPANEL_CONFIG_DIR=/path/to/restored-config \
     /opt/mcpanel/McPanel.Api --mcpanel-import-export /path/to/instances.zip
   ```

   Run with the panel service account and readable archive permissions. Import
   rejects conflicting IDs, names, or ports before moving any instance files.
   Imported instances are stopped; autostart, crash recovery, and schedules are
   disabled. Gate configuration is regenerated from imported relationships.
4. Review the restored configuration for host-specific paths, Java executables,
   IP addresses, proxy names, and mount locations. Restore file ownership to the
   panel service account. Install using these recovered data/config directories.
   Recovered sessions are revoked; autostart, crash recovery, and schedules are
   disabled until reviewed. The administrator password is retained.
5. Verify readiness, select Java runtimes, start each server, and check world
   progress, modpack metadata, Gate routing, and authentication. Enable the
   desired schedules and automatic recovery. Capture and verify a new off-host
   panel backup and a separate instance export before declaring recovery complete.

A server export contains the instance, launch metadata, and modpack baseline.
Create it from Backups and download it from Activity. With the panel stopped,
run `--mcpanel-import-export EXPORT` using the destination installation's
`MCPANEL_DATA_DIR` and `MCPANEL_CONFIG_DIR`. Conflicting IDs, names, ports, or
existing directories are rejected. Gate relationships require a whole-panel
bundle. Inspect imported Java settings before starting.

## Account recovery and incidents

Run `mcpanel reset-admin` from a local terminal with sudo access. The manager stops
the panel, requests a hidden password, updates only the administrator hash and
session stamp, records the reset, and restarts the panel. Workloads in the runtime
continue running. Custom installations can invoke `--mcpanel-reset-admin` as root
with their data/config environment after stopping their panel unit.

Activity retains jobs across navigation and restarts. A queued action is not a
completed action. Running backups support cancellation; destructive operations
can only be canceled before they start. Interrupted operations need review.
Supported retries validate current server state and retain a link to the prior
job. Servers with failed recovery remain blocked until journal recovery succeeds.
Use Retry recovery after repairing the underlying filesystem problem. Preserve
staging journals, software rollback directories, and backup files for repair.

Audit records include authentication requests, commands, file changes, restores,
deletions, and configuration mutations, with actor, route, outcome, time, and
correlation ID. Console commands are retained, including their arguments.
Authentication request bodies and file contents are excluded. Default audit
retention is 90 days; protect the host journal as the fallback when database
writes fail.

## Upgrade and rollback

The installer defaults to stable releases. Select `--release vX.Y.Z` to pin an
immutable release; `--release main` explicitly opts into rolling development.
Stable tags require release notes and acceptance evidence; their assets cannot
be replaced by the workflow. Enable GitHub's immutable-release protection and
protect version tags and the `production-release` environment before publication.
The [GitHub release API](https://docs.github.com/en/rest/releases/releases) defines
latest stable selection separately from prereleases.

Schema 2 adopts populated legacy schema 1 through an additive transaction, keeping
a consistent pre-migration database snapshot in `data/schema-backups`. Runtime
protocol 1 and console schema 1 remain unchanged. The staged binary checks schema
and active runtime compatibility before replacement. This first runtime upgrade
requires idle workloads if the old runtime lacks save leases; later compatible
runtimes can remain active while the panel updates. An incompatible or unknown
schema fails closed. Never delete a database to force an upgrade.

The previous binaries remain in a dated sibling rollback directory. For rollback
within this additive schema transition, stop the panel and run the current binary
with `--mcpanel-prepare-rollback` using the installation data/config environment.
This snapshots state, translates new terminal job states for older readers, and
refuses rollback while recovery-required servers exist. Then preserve the current
installation, restore the prior binaries, and start the panel. Automatic update
rollback runs this same check and leaves the panel stopped if it cannot pass. Leave `state.db`
and `console.db` in place: the previous application ignores additive tables and
columns, and the console schema is unchanged. Never restore an old database over
newer world progress as an automatic rollback. If a future release changes this
compatibility, its preflight must reject the update until an explicit data
recovery procedure is supplied. Retain pre-migration snapshots for manual repair.

## Operational acceptance run

Run `sudo python3 tests/production-soak.py --hours 24 --evidence /var/tmp/mcpanel-soak-UNIQUE`
from a checkout matching the installed release. The harness requires an existing,
stopped, EULA-accepted Vanilla server as a source. It copies only the JAR and
launch metadata, creates a separate world and administrator, uses separate
systemd services and ports, and preserves the original installation. It checks
real cgroup enforcement, repeated backups, panel death during save suspension,
rejection of interrupted snapshots, runtime recovery while the panel is offline,
explicit stops, and console retention. Its result and journals remain in the
evidence directory. A short smoke run is useful but is not a 24-hour acceptance.
Keep the matching immutable MC Panel release assets and Java installers alongside
off-host bundles so a clean-host drill can proceed without upstream downloads.

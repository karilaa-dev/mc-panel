# MC Panel HTTP and realtime contract

The canonical API base is `/api/v1`. For the bundled web client, `/api/*` is a
compatibility alias with the same behavior. JSON uses camel-case names. All
authenticated mutating requests require the `X-XSRF-TOKEN` value returned by
`GET /api/v1/auth/antiforgery`. Errors use `application/problem+json` and add a
stable `code` property.

Authentication uses one `mcpanel.auth` HTTP-only same-site cookie. Before the
first admin exists, `POST /auth/setup` accepts the installer setup token,
username, and password. The setup token can only be used once.

Authentication cookies carry a persistent session stamp. Changing the password
rotates that stamp and reissues the caller's cookie, invalidating every older
copy. Logging out rotates the stamp and invalidates all current copies. A fresh
cookie remains valid across normal panel restarts; a stale cookie cannot access
HTTP routes or negotiate a new SignalR connection. A rotation also removes
already-connected stale clients from the realtime broadcast audience and sends
them `SessionRevoked`; the bundled client immediately returns to authentication.

## Routes

| Method | Route | Result / body |
|---|---|---|
| GET | `/auth/status` | `{setupRequired, authenticated, admin?}` |
| GET | `/auth/antiforgery` | `{token}` and XSRF cookie |
| POST | `/auth/setup` | `{token, username, password}` -> admin |
| POST | `/auth/login` | `{username, password}` -> admin |
| POST | `/auth/logout` | `204` |
| PUT | `/auth/password` | `{currentPassword, newPassword}` -> `204` |
| GET | `/servers` | `ServerSummary[]` |
| POST | `/servers` | `CreateServerRequest` -> `202 Job` (including `serverId`) |
| POST | `/servers/modpack` | inspected `CreateModpackServerRequest` -> `202 Job` |
| POST | `/servers/gate` | `{name,port,startOnBoot,clientRequestId}` -> verified Gate install `202 Job` |
| GET/DELETE | `/servers/{id}` | summary / `204`; delete requires no process and removes managed files plus backups |
| PUT | `/servers/{id}/public-address` | `{address?,expectedRevision}` -> updated server summary |
| POST | `/servers/{id}/actions/{start|stop|restart|update}` | `202 Job` |
| POST | `/servers/{id}/actions/kill` | `{confirm:true}` -> `202 Job` emergency process-tree kill |
| POST | `/servers/{id}/{start|stop|restart|update}` | compatibility alias for bundled client |
| GET/PUT | `/servers/{id}/properties` | versioned, sectioned effective entries and available catalog definitions / `{revision,values,acknowledgedIncompatibleKeys?}` |
| GET/PUT | `/servers/{id}/runtime` | `{initialMemoryMb,maximumMemoryMb,totalMemoryMb,javaRuntimeId,jvmArguments,useAikarFlags,startOnBoot,crashRecovery}` |
| GET/PUT/DELETE | `/servers/{id}/icon` | 64×64 PNG / multipart `file` -> `{revision}` / `204` |
| PUT | `/servers/{id}/icon/library` | apply a reusable panel icon by `{revision}` |
| GET/POST | `/icons` | reusable panel icon metadata / multipart `file` -> `{revision}` |
| GET/DELETE | `/icons/{revision}` | reusable 64×64 PNG / remove the library copy |
| GET/PUT | `/servers/{id}/configuration` | deprecated curated compatibility adapter |
| GET | `/servers/{id}/console?after=&limit=` | ordered console events |
| POST | `/servers/{id}/console` | `{command}` -> `204` |
| GET | `/servers/{id}/players` | observed players merged with authoritative whitelist, operator, and ban JSON files |
| POST | `/servers/{id}/players/{name}/{whitelist|unwhitelist|op|deop|ban|pardon|kick}` | resulting `PlayerDto` |
| GET | `/servers/{id}/players/{uuid}/inventory` | read-only fixed saved inventory slots; available while online as the latest on-disk save |
| GET | `/servers/{id}/players/{uuid}/inventory/backups` | latest inventory-only recovery snapshots |
| POST | `/servers/{id}/players/{uuid}/inventory/backups` | `{expectedRevision}` -> inventory-only snapshot; allowed while online |
| GET | `/servers/{id}/players/{uuid}/inventory/backups/{backupId}` | read-only slot preview of an inventory-only snapshot |
| POST | `/servers/{id}/players/{uuid}/inventory/backups/{backupId}/restore` | `{expectedRevision}` -> restored inventory |
| GET | `/servers/{id}/mods` | Fabric/Forge/NeoForge top-level JAR metadata as `ModFileDto[]` |
| POST | `/servers/{id}/mods/modrinth` | `{projectId,versionId,selectedDependencyProjectIds?}` -> verified install `202 Job` |
| GET | `/servers/{id}/plugins` | Paper top-level plugin JAR metadata as `ModFileDto[]` |
| POST | `/servers/{id}/plugins/modrinth` | `{projectId,versionId,selectedDependencyProjectIds?}` -> verified install `202 Job` |
| GET | `/servers/{id}/modpack/changes` | retained pack baseline summary and changed files |
| GET | `/servers/{id}/files?path=` | directory entries |
| POST | `/servers/{id}/files` | `{path,directory}` -> `204` |
| GET/PUT | `/servers/{id}/files/content?path=` | text content / `{content}` |
| GET | `/servers/{id}/files/download?path=` | streamed file |
| POST | `/servers/{id}/files/upload?path=` | multipart `file` -> `204` |
| POST | `/servers/{id}/files/move` | `{source,destination}` -> `204` |
| POST | `/servers/{id}/files/extract` | `{path,destination}` -> `204` |
| DELETE | `/servers/{id}/files?path=` | `204` |
| GET/POST | `/servers/{id}/backups` | backups / `202 Job` |
| GET/DELETE | `/servers/{id}/backups/{backupId}` | download / `204` |
| POST | `/servers/{id}/backups/{backupId}/restore` | `202 Job` (stopped only) |
| GET/POST | `/servers/{id}/schedules` | schedules / schedule |
| PUT/PATCH/DELETE | `/servers/{id}/schedules/{scheduleId}` | schedule / toggle / `204` |
| GET | `/jobs/{id}` | durable operation status, including the related `serverId` when applicable |
| GET | `/java` | discovered Java runtimes |
| POST | `/java/rescan` | refreshed runtimes |
| POST | `/java/custom` | `{path}` -> validated runtime |
| GET | `/catalog?experimental=false` | Vanilla/Paper/Fabric/Forge/NeoForge version arrays plus detailed build choices |
| GET | `/modrinth/search?projectType=&query=&offset=&limit=&serverId=&gameVersion=&loader=` | paginated mod, plugin, or modpack projects |
| GET | `/modrinth/projects/{projectId}/versions?serverId=&projectType=&gameVersion=&loader=` | filtered release, beta, and alpha versions with resolved dependency titles, links, and installed-version matches |
| POST | `/modrinth/modpacks/imports/modrinth` | `{versionId}` -> inspected single-use pack token |
| POST | `/modrinth/modpacks/imports/upload` | multipart `.mrpack` -> inspected single-use pack token |
| GET | `/system/status` | host CPU, memory, disk, and recent samples |
| GET | `/system/info` | panel paths/version/allocation ceiling |
| GET/PUT | `/system/settings` | `{keepServersRunningOnPanelStop,globalServerHost,revision}` |
| GET | `/servers/{id}/gate` | one Gate server's installation, runtime, configuration, routes, warnings, and connection statistics |
| PUT | `/servers/{id}/gate/config` | expected revision, listener/start/recovery settings, Lite/classic mode, managed/external backends, default, and forwarding |
| POST | `/servers/{id}/gate/update` | `{confirmDisconnectPlayers}` -> `202 Job` |
| POST | `/servers/{id}/gate/secrets/{velocity|bungeeguard}/generate` | `{confirmReplace}` -> new instance-local secret with `Cache-Control: no-store` |
| POST | `/servers/{id}/gate/secrets/{velocity|bungeeguard}/{reveal|rotate}` | reveal or compatibility rotation of the instance-local secret with `Cache-Control: no-store` |
| any | `/system/gate...` | `410 GATE_API_REPLACED`; select a Gate server instead |

`POST /servers` accepts `name`, `kind` (`Vanilla`, `Paper`, `Fabric`, `Forge`, `NeoForge`),
`version`, `javaRuntimeId`, `memoryMb`, `port`, `eulaAccepted`, optional
`startOnBoot`, optional Paper `build`, optional Fabric `loaderVersion` and
`installerVersion`, or a Forge/NeoForge `loaderVersion`. `memoryMb` is the user-selected JVM RAM value, has a 512 MiB
minimum, and uses 512 MiB increments. MC Panel applies it equally to Xms and
Xmx, then derives a larger internal cgroup ceiling for native-memory headroom.
Legacy entries below the minimum must be raised in Runtime before they can
start. Only new, verified upstream installs are accepted; arbitrary source
directories and URLs are never adopted.

All three create contracts accept an optional client-generated UUID
`clientRequestId`. The server row and queued job are committed in one
transaction; a retry with the same key returns that original pair. Queue handoff
failure marks the committed job/server failed but still returns the truthful
committed result. Deletion first validates default-backend constraints and
stages the exact instance/backup directories, then removes rows and Gate
memberships in one transaction. Files are restored if the transaction fails;
post-commit cleanup and asynchronous Gate reconciliation cannot turn a committed
delete into an error response.

`POST /servers/modpack` accepts `name`, `importToken`, `javaRuntimeId`,
`memoryMb`, `port`, `eulaAccepted`, optional `startOnBoot`, and optional
`selectedOptionalFiles`. Import tokens expire after one hour and are claimed
once. The inspected `.mrpack` determines Minecraft and the loader; Fabric,
Forge, NeoForge, and loader-free Vanilla packs are supported. Client-unsupported
files and `client-overrides` are skipped, selected optional files are installed,
and common overrides are applied before `server-overrides`.

The properties API updates existing entries and can append missing keys from the
checked-in historical catalog. It requires the revision returned by GET and an
explicit acknowledgement before adding a property outside the server version's
supported ranges. Arbitrary new keys are rejected, while uncatalogued keys that
already exist remain editable. Comments, blank lines, ordering, unknown keys,
and effective last-duplicate behavior are preserved. A changed file is rejected
with `CONFIGURATION_CHANGED`. The deprecated configuration adapter
continues to enforce its curated limits of 512 characters for the MOTD, 128 characters
and 255 UTF-8 bytes for the world directory name, and 2,048 characters for
additional JVM arguments. Schedule names are limited to 96 characters. Control
characters that could inject extra properties or commands are rejected.

Runtime memory exposes one value through `maximumMemoryMb`; saving normalizes
`initialMemoryMb` to the same value. `totalMemoryMb` remains in the compatibility
DTO as the derived internal ceiling. Native-memory headroom is 25% of the
selected heap, rounded to 512 MiB steps, with a 512 MiB minimum and 4 GiB
maximum. Total workload memory is enforced with cgroup
v2 `memory.max`, with `memory.high` used as an earlier reclaim threshold and swap
disabled for the server cgroup. JVM launch order is managed Xms, managed Xmx, the optional
non-memory Aikar preset, custom JVM arguments, then either managed `-jar` or
argument-file launch input and `nogui`
arguments. Custom arguments cannot contain Xms, Xmx, or other managed launch
arguments. Server summaries report cgroup current, peak, and swap memory plus
whether enforcement is active; non-production development runs fall back to
the Java process working set.

The mods inventory is live, read-only, and available only to Fabric, Forge, and
NeoForge instances. It scans regular, non-symlinked `mods/*.jar` files without
extracting them and recognizes `fabric.mod.json`, Forge `META-INF/mods.toml`,
legacy `mcmod.info`, and NeoForge `META-INF/neoforge.mods.toml` with a
transitional `mods.toml` fallback. A malformed or unknown JAR is represented by
its own `Invalid`, `Partial`, or `Unrecognized` result rather than failing the
inventory.

Paper exposes the same live inventory for regular, non-symlinked
`plugins/*.jar` files and recognizes `paper-plugin.yml` and `plugin.yml`.
Modrinth browsing defaults to the server's Minecraft version and loader but
accepts explicit catalog filters. Installation always revalidates against the
actual server; Paper accepts Paper-compatible Bukkit, Spigot, and Purpur plugin
versions. A selected primary JAR is checksum-verified and activated atomically
under `mods/` or `plugins/`. Required Modrinth project dependencies are
selectable, preselected by default, resolved to exact or latest compatible
versions, checksum-verified in the same staging operation, and activated with
the selected mod or plugin. Existing top-level JARs are matched to Modrinth
projects by SHA-512. Installed dependencies are reported with their versions,
left unselected, and retained instead of installing another version; attempts
to install a different version of the primary project fail with a clear
conflict. Filename-only external dependencies remain informational. A live
install is allowed and sets `restartRequired`.

Servers created from a modpack retain the original `.mrpack` and a normalized
SHA-512 baseline outside the instance directory. The changes endpoint hashes
tracked paths on demand and reports `Modified` or `Removed`, plus regular
non-symlinked top-level `mods/*.jar` files as `Added`. Unselected optional files
and newly generated configs, worlds, logs, libraries, and runtime files are not
reported.

Server summaries include nullable `iconRevision`; clients use it for presence
detection and cache-busted icon URLs. Icon uploads are PNG multipart payloads
whose signature and 64×64 IHDR are validated, with a 256 KiB cap. Replacement
and deletion use atomic file activation with database rollback. Changes made to
a running server set `restartRequired`.

`whitelist.json`, `ops.json`, and `banned-players.json` are authoritative for
player membership. Running servers are changed through console commands;
stopped or crashed servers are changed with locked atomic file replacement.
Online-mode nickname resolution uses the fixed Minecraft profile lookup service,
while offline-mode servers derive Java's standard offline UUID.

## Gate servers and advertised addresses

`Gate` is a normal `ServerKind`; administrators may create multiple instances.
Each has its own server ID, real listener port, regular lifecycle and console,
fixed 256 MiB cgroup, instance directory, configuration, API port, binaries,
rollback copies, logs, and forwarding secrets. Creation and updates resolve the
newest complete stable Minekube release for the host's exact Linux architecture,
require `checksums.txt`, enforce bounded downloads, verify SHA-256, run
`gate --version`, and atomically activate only that instance. A failed running update
restores and restarts the preceding binary.

Panel Settings stores one normalized global hostname/IP. Every server may also
store a normalized advertised connection-address override. A host-only override
means port 25565; an explicit `host:port` or `[IPv6]:port` preserves that public
port. With no override, summaries derive `global host + Server.Port`. Port 25565
is omitted when copied and IPv6 is bracketed when a suffix is shown. Advertised
ports are never probed because they can represent NAT, SRV, or an external
forward; only real Minecraft and Gate listener ports are conflict-checked.

Each Gate configuration has an optimistic revision and a separate Backends page.
It can select panel-managed non-Gate servers and add arbitrary reachable
Minecraft backends by name plus `host`, `host:port`, `[IPv6]`, or
`[IPv6]:port`. A managed backend may belong to multiple Gate servers. The Gate
server's advertised hostname is its default route. A selected managed backend's
explicit advertised hostname becomes an exact/forced host route, while its
advertised port does not alter Gate's real listener. External backends are
reachable through the default route or Classic `/server`. Duplicate route
hostnames are rejected within one Gate instance but may be reused by another
instance.

Lite emits exact routes only and has no unknown-host fallback. Classic registers
only that Gate's selected backends with stable names, puts only its default in
`try`, and supports `/server`. Velocity, BungeeGuard, Legacy, and None are
independent per instance. Random Velocity/BungeeGuard secrets are mode `0600`
instance files and never appear in ordinary DTOs or logs. They are created only
through an explicit Generate secret action; replacing an existing secret
requires confirmation. Classic Velocity/BungeeGuard start requires the selected
secret to exist, but has no acknowledgement checkbox. Managed config files are
atomically replaced and Gate's documented config watcher validates and
live-applies valid changes.

The Gate Settings UI keeps listener/workload mode on General and exposes the
current Java Classic configuration surface on a separate Classic tab. The tab
is disabled while Lite is selected because Lite ignores authentication,
forwarding, status/query, failover, quotas, packet limits, compression, proxy
protocol, proxy commands, and Via translation. Panel-owned bind, backend,
forced-host, Lite route, secret, and loopback API fields remain generated from
the server and Backends pages rather than duplicated as raw inputs.

Deleting a default backend is blocked with every affected Gate name. Deleting a
non-default backend removes only its memberships and marks those Gate instances
dirty. The authoritative server mutation commits independently from asynchronous
Gate reconciliation. Legacy `/system/gate` endpoints return
`GATE_API_REPLACED`.

The persistent runtime advertises typed-workload capabilities. A legacy
`Unknown runtime operation` response is classified internally and triggers the
existing idle-upgrade handshake. Existing Minecraft lifecycle calls remain
available while active processes delay replacement; Gate lifecycle/update calls
return `RUNTIME_UPGRADE_PENDING` until the current typed runtime is active.

## Player inventory files

`PlayerDto.inventoryAvailable` indicates a known UUID with player data and
`inventorySavedAt` reports its file timestamp. Inventory GET accepts gzip NBT
only (8 MiB compressed and 32 MiB decompressed limits), rejects traversal and
symlinked path segments, and maps the fixed hotbar, storage, armor, offhand, and
Ender Chest slots. It understands legacy numeric IDs and `Count`/`tag` as well
as modern namespaced IDs and `count`/`components`. Metadata is summarized but
raw NBT and unrelated player statistics are never exposed. Both the traditional
`<world>/playerdata/<uuid>.dat` layout and Minecraft 26's
`<world>/players/data/<uuid>.dat` layout are recognized. Missing Inventory or
EnderItems tags and empty lists with any serialized element marker are treated
as empty slot sets, covering new players that have not used an Ender Chest.

Viewing an online player returns Minecraft's last saved snapshot with an
explicit staleness warning. A manual or scheduled inventory backup may also
capture that on-disk snapshot while the player is online. Manual snapshots use
the displayed revision so a concurrent Minecraft save is reported as
`PLAYER_DATA_CHANGED` instead of producing a misleading snapshot. PUT and
snapshot restore require the player to be offline and the server to be in a
normal Running or Stopped state. They acquire
the server mutation lock, compare the SHA-256 revision of the compressed file,
validate every slot, create an inventory-only recovery snapshot, write and
flush a same-directory temporary gzip NBT file, reload it for verification,
then atomically replace the original. Existing stacks can name their original
source slot so their complete unknown NBT payload follows a move or edit. New
stacks contain only ID/count fields; `clearMetadata` removes legacy `tag` or
modern `components`. All unrelated root tags remain semantically unchanged.
Only the latest 20 inventory snapshots per player are retained beneath the
server backup tree, and restoring one changes only `Inventory` and
`EnderItems`.

Configuration and file mutations are serialized with lifecycle operations and
are accepted only while database state and supervised-process state form a
stable combination (`Stopped`, `Running`, or `Crashed`). Manual backup creation
is accepted only for a live `Running` server or a process-free `Stopped` server;
restore requires `Stopped`. Backup archives are committed only after the same
entry-count and uncompressed-size limits used by restore have passed.

The SignalR hub is `/hubs/panel`; authenticated clients receive
`ConsoleBatch(ConsoleEvent[])`, `ServerStateChanged(ServerSummary)`,
`MetricsUpdated({host,servers})`, and `JobUpdated(Job)`. Clients reconnect to
the durable console with its numeric `sequence` cursor.

A successful start job waits for Minecraft's normal `Done (` readiness output.
For modded servers that replace that message, a still-running process is accepted
after a 90-second fallback. Stop and emergency kill remain available during that
readiness window.

Schedules use a time-only trigger, not arbitrary shell commands. A schedule
has `frequency` (`Once`, `Interval`, `Daily`, `Weekly`, or `Cron`), timezone,
trigger fields (`runAt`, `intervalMinutes`, `timeOfDay`, `daysOfWeek`, or a
restricted five-field cron string), and ordered `actions`. Each action is one
of `Start`, `Stop`, `Restart`, `Backup`, `InventoryBackup`, `Update`, or
`Command`. `InventoryBackup` snapshots every saved player inventory on the
server; it does not accept a player selector. `Command` requires a one-line
console command. Overlapping and missed executions are not replayed.

## Stable problem codes

`AUTH_INVALID`, `SETUP_DISABLED`, `SETUP_TOKEN_INVALID`,
`ANTIFORGERY_FAILED`, `VALIDATION_FAILED`, `NOT_FOUND`, `SERVER_BUSY`,
`SERVER_NOT_STOPPED`, `SERVER_NOT_RUNNING`, `JAVA_RUNTIME_NOT_FOUND`,
`JAVA_VERSION_INCOMPATIBLE`, `PORT_IN_USE`, `MEMORY_LIMIT_EXCEEDED`,
`MEMORY_LIMIT_TOO_LOW`,
`INSTALL_DOWNLOAD_REJECTED`, `INSTALL_CHECKSUM_FAILED`, `UPSTREAM_UNAVAILABLE`,
`CONFIGURATION_CHANGED`, `PROPERTY_VERSION_ACKNOWLEDGEMENT_REQUIRED`,
`PLAYER_NOT_FOUND`, `PLAYER_LIST_INVALID`, `ICON_TOO_LARGE`,
`PATH_OUTSIDE_SERVER`, `FILE_TOO_LARGE`, `ZIP_LIMIT_EXCEEDED`, and
`OPERATION_FAILED`. Gate operations additionally use `GATE_NOT_INSTALLED`,
`GATE_CONFIG_CHANGED`, `GATE_CONFIG_INVALID`, `GATE_PORT_IN_USE`,
`GATE_BACKEND_SETUP_UNCONFIRMED`, `GATE_RELEASE_UNAVAILABLE`, and
`GATE_CHECKSUM_MISMATCH` (with `GATE_ACTIVE_CONNECTIONS` for unconfirmed live
updates), `GATE_DEFAULT_SERVER`, `GATE_API_REPLACED`, and
`RUNTIME_UPGRADE_PENDING`; advertised-address mutations use
`CONNECTION_ADDRESS_INVALID`; inventory operations use `PLAYER_DATA_NOT_FOUND`,
`PLAYER_DATA_INVALID`, `PLAYER_DATA_CHANGED`, and `PLAYER_ONLINE`.

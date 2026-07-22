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
| GET/DELETE | `/servers/{id}` | summary / `204`; delete requires no process and removes managed files plus backups |
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
| GET | `/servers/{id}/mods` | Fabric/Forge/NeoForge top-level JAR metadata as `ModFileDto[]` |
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
| GET | `/system/status` | host CPU, memory, disk, and recent samples |
| GET | `/system/info` | panel paths/version/allocation ceiling |
| GET/PUT | `/system/settings` | `{keepServersRunningOnPanelStop}` |

`POST /servers` accepts `name`, `kind` (`Vanilla`, `Paper`, `Fabric`, `Forge`, `NeoForge`),
`version`, `javaRuntimeId`, `memoryMb`, `port`, `eulaAccepted`, optional
`startOnBoot`, optional Paper `build`, optional Fabric `loaderVersion` and
`installerVersion`, or a Forge/NeoForge `loaderVersion`. `memoryMb` is the user-selected JVM RAM value, has a 512 MiB
minimum, and uses 512 MiB increments. MC Panel applies it equally to Xms and
Xmx, then derives a larger internal cgroup ceiling for native-memory headroom.
Legacy entries below the minimum must be raised in Runtime before they can
start. Only new, verified upstream installs are accepted; arbitrary source
directories and URLs are never adopted.

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
of `Start`, `Stop`, `Restart`, `Backup`, `Update`, or `Command` with an optional
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
`OPERATION_FAILED`.

# HTTP and realtime API

The main API prefix is `/api/v1`. `/api` is a compatibility alias used by the
bundled web client. JSON property names use camel case.

This file records the protocol rules and route groups. The request and response
types in `Contracts/Dtos.cs` and the mappings in `ApiEndpoints.cs` define the
exact fields.

## Authentication

MC Panel uses one HTTP-only, same-site cookie named `mcpanel.auth`.
`POST /auth/setup` creates the first administrator with the installer token.
The endpoint stops accepting the token after that account exists.

Every state-changing API request must send the `X-XSRF-TOKEN` returned by
`GET /auth/antiforgery`. This includes setup, login, and file uploads.

Changing the password or logging out invalidates older cookies and connected
realtime clients. Errors use `application/problem+json` and include a stable
`code` field.

## Route groups

All paths below use the `/api/v1` prefix.

| Prefix | Purpose |
| --- | --- |
| `/auth` | Setup, login, logout, password changes, and antiforgery tokens |
| `/servers` | Server creation, settings, lifecycle, console, and deletion |
| `/servers/{id}/properties` | Versioned `server.properties` editing |
| `/servers/{id}/runtime` | Java, memory, JVM arguments, and recovery settings |
| `/servers/{id}/software` | Regular-server software metadata and stopped-only core changes |
| `/servers/{id}/players` | Player actions and saved inventory access |
| `/servers/{id}/files` | Confined file listing, editing, upload, and download |
| `/servers/{id}/backups` | Backup creation, download, restore, and deletion |
| `/servers/{id}/schedules` | Timed server actions |
| `/servers/{id}/mods` | Mod inventory and Modrinth installation |
| `/servers/{id}/plugins` | Paper plugin inventory and Modrinth installation |
| `/servers/{id}/gate` | Gate configuration, updates, backends, and secrets |
| `/modrinth` | Project search, versions, and modpack imports |
| `/java` | Runtime discovery and custom executable validation |
| `/catalog` | Minecraft distributions, versions, and builds |
| `/server-jars/imports` | Single-use executable Custom JAR uploads |
| `/jobs` | Status for queued operations |
| `/system` | Host metrics, panel information, and global settings |

Server creation and lifecycle operations return `202 Accepted` with a durable
job. A client may give creation requests a UUID named `clientRequestId`.
Repeating the UUID returns the first server and job instead of creating another
server.

`CustomJar` is a regular server kind. A custom creation consumes one upload
token from `POST /server-jars/imports`; tokens expire after one hour. A software
change accepts either one upload token or one safe in-instance JAR path, never
both. `POST /servers/{id}/software/change` requires a stopped non-Gate server,
can create a backup before staging, and returns a durable `ChangeSoftware` job.
Manual changes preserve instance content and clear Modrinth pack linkage.

Writes that can race with another browser or filesystem change use an expected
revision. A stale revision returns a conflict instead of overwriting newer
data. File operations reject paths outside the selected server and reject
symlink escapes.

Gate is a normal server kind. Each Gate has its own listener, configuration,
backends, secrets, logs, and rollback copy. Old `/system/gate` routes return
`410 GATE_API_REPLACED`.

Inventory reads return Minecraft's most recent saved file, which may lag behind
an online player. Inventory restores require that player to be offline. MC
Panel keeps the 20 newest inventory recovery snapshots per player.

## Realtime events

The authenticated SignalR hub is `/hubs/panel`. It sends these events:

- `ConsoleBatch`
- `ServerStateChanged`
- `MetricsUpdated`
- `JobUpdated`
- `SessionRevoked`

Console clients reconnect with the numeric `sequence` cursor returned by the
console API.

## Schedules

Schedules support `Once`, `Interval`, `Daily`, `Weekly`, and restricted
five-field `Cron` triggers. Actions are `Start`, `Stop`, `Restart`, `Backup`,
`InventoryBackup`, `Update`, or `Command`. Commands must fit on one line.
Missed and overlapping runs are not replayed.

## Error codes

Clients should branch on the `code` field, not the message. Common codes are
`AUTH_INVALID`, `ANTIFORGERY_FAILED`, `VALIDATION_FAILED`, `NOT_FOUND`,
`SERVER_BUSY`, `SERVER_NOT_STOPPED`, `JAVA_VERSION_INCOMPATIBLE`, `PORT_IN_USE`,
`MEMORY_LIMIT_EXCEEDED`, `INSTALL_CHECKSUM_FAILED`, `UPSTREAM_UNAVAILABLE`,
`CONFIGURATION_CHANGED`, `PATH_OUTSIDE_SERVER`, `FILE_TOO_LARGE`,
`ZIP_LIMIT_EXCEEDED`, and `OPERATION_FAILED`.

Feature-specific codes are declared next to the checks that return them. Search
for `PanelException` and `PanelProblems` when adding client behavior for one of
those failures.

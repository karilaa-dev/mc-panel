# Production readiness implementation

Target: one trusted administrator, private single host, off-host recovery point
no older than one hour. Manual host recovery is supported. This document tracks
implementation and evidence; unchecked acceptance items are not production claims.

## Work

- [x] Versioned schema adoption, consistent snapshots, update compatibility.
- [x] Durable recovery blocks and runtime-owned save-suspension leases.
- [x] Storage-independent process control, bounded logging and retention.
- [x] Workload memory reservations, runtime crash recovery, truthful readiness.
- [x] Large backups, capacity admission, retention, cancellation and exports.
- [x] Download deadlines, durable jobs, safe retry and cancellation.
- [x] Whole-panel recovery and a network-filesystem replication/verification mechanism.
- [ ] Configure and verify an actual off-host destination.
- [x] Activity, health, scheduling history, alerts and audit trail.
- [x] File conflicts, draft protection, session and console reconnection.
- [x] Privileged account recovery, private HTTPS, stable release workflow.
- [x] Regression checks, deployment and readiness verification.
- [ ] Clean-host recovery drill and 24-hour systemd/cgroup operational test.

## Baseline

Revision 8476cd2: 317 API tests and 132 frontend tests passed. Frontend type
checking, lint and build, ShellCheck and installer tests passed. No fault-injection
or clean-host/24-hour acceptance claim was made for that revision.

## Evidence from this implementation

- The full Release API suite passed all 342 tests. All 138 frontend tests passed,
  along with type checking, lint, the production build, ShellCheck, and installer
  and bootstrap tests. Logs: `/var/tmp/mcpanel-production-api-final.log` and
  `/var/tmp/mcpanel-production-web-tests.log`.
- Two additional SQLite-lock cases then passed in the 15-test runtime suite:
  graceful stop and forced kill both completed while console writes were locked.
  Log: `/var/tmp/mcpanel-runtime-lock-tests.log`.
- Schema tests cover fresh state, populated prior state, retired installed fields,
  future/unknown schema rejection, console writer compatibility, and consistent
  pre-migration snapshots. Deployment exercised adoption of this host's populated
  database without deleting its dormant legacy tables or columns.
- Backups larger than 2 GiB were created and restored, including a retention
  budget smaller than the protected backup. Fault tests cover lease expiry,
  unacknowledged save resumption, dropped logging/storage failure, and a child
  which never confirms startup. Four stalled download workers time out and later
  queued work executes.
- Recovery tests verify a clean data directory using a self-contained bundle,
  world/launch metadata/key preservation, checksum and traversal rejection, session
  revocation, and rollback refusal for servers requiring repair.
- UI regressions cover stale revision drafts, navigation/back protection, expired
  authentication, failed list requests, persisted job failure, and restarting
  console reconnection after automatic retries are exhausted.
- A real Minecraft 26.2 smoke cycle passed under isolated systemd services with
  cgroup enforcement on 2026-09-05. It verified panel death during save suspension,
  rejected the interrupted job, resumed saving, recovered a real process crash
  while the panel was offline, and completed explicit stop/start. Evidence:
  `/var/tmp/mcpanel-soak-smoke-20260905/result.json` and `events.jsonl`.
- Caddy validated `deploy/Caddyfile.private`. A separate live proxy test used its
  internal CA and verified HTTPS, Secure/HttpOnly/SameSite cookies, and HTTP 426
  for direct API requests. Evidence: `/var/tmp/mcpanel-https-alp1hn6q/result.json`.
- A rollback drill started the previous binaries against a preserved schema-2
  database after rollback preparation. Authentication, existing server reads,
  and translation of a new terminal job state succeeded. Evidence:
  `/var/tmp/mcpanel-rollback-drill-8acy8pwx/result.json`.
- The current checkout was installed with `./mcpanel.sh update --source local`.
  Both systemd services are active, and `http://192.168.1.37:6050/health/ready`
  confirms schema 2 and compatible panel/runtime versions. Configuration, data,
  and previous binaries were retained; the original Minecraft server is stopped.
- The final-build 24-hour run started on 2026-09-05 at 04:47:57 UTC under
  `mcpanel-production-acceptance-final-20260905.service`. Results are recorded in
  `/var/tmp/mcpanel-production-24h-final-20260905/result.json` and `events.jsonl`.
  It cannot satisfy the duration gate before approximately 2026-09-06 04:48 UTC.
  Its first cycle passed with enforced cgroup limits, interrupted-backup rejection,
  save resumption, panel-offline crash recovery, and a retention check.
  The earlier run was deliberately stopped for the final runtime change and
  provides no 24-hour acceptance evidence.
- The Gate fixes superseded that build after 12 successful cycles. A fresh run
  uses `mcpanel-production-acceptance-gatefix-20260905.service`, with results under
  `/var/tmp/mcpanel-production-24h-gatefix-20260905`. The harness now copies the
  installed binaries into its evidence directory, so later panel updates cannot
  silently change the build being tested. No completed 24-hour result is claimed.
- Gate version selection, mode preparation, memory admission, and live protocol
  checks are documented in [Gate setup](deploy/GATE.md). The current suite has
  349 passing API tests and 141 passing frontend tests.
- npm audit reports zero vulnerabilities after updating the affected transitive
  dependencies. T3 browser preview initialization failed; no visual browser pass
  is claimed. Component tests and HTTP/static-asset checks provide the UI evidence.

## Release gates still requiring evidence

- No off-host destination was supplied. Replication, the one-hour off-host recovery
  point, and restoration on a separate clean host are not established. Keep the
  matching release and Java installers with off-host bundles for an offline drill.
- The 24-hour operational run must finish successfully; a smoke cycle does not
  satisfy it. A release also needs reviewed version-specific notes and acceptance
  evidence before its immutable tag can be published.
- Before publication, record a bounded-filesystem disk-full drill and an upgrade
  with live workloads from the intended previous release. Storage-write fault
  tests and schema compatibility tests do not establish those deployment results.
- The HTTPS example was tested but has not replaced this installation's private
  HTTP access. Configure private DNS/CA trust, the proxy, alerts, and the off-host
  mount for this installation before production sign-off.

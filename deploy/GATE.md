# Gate versions and backend setup

Choose a stable Gate version when creating a proxy, or use the release picker on
its Gate settings page. Releases must include the host's binary and Minekube's
checksum manifest. Version changes are jobs in Activity; the previous binary is
retained for rollback. A running proxy restarts during a version change.

## Classic and Lite

Classic authenticates players at Gate. Its Minecraft backends must accept proxy
connections without authenticating the same session again. Lite forwards the
connection, including authentication, to the backend.

To configure a managed backend or change modes:

1. Stop Gate and all its selected managed backends.
2. Save the desired mode in Gate settings.
3. Select **Prepare backends for Classic** or **Prepare backends for Lite** and
   review the changes.
4. Start the backends, then Gate. Join using Gate's advertised address and port.

Classic preparation enables `online-mode=false`, disables backend secure-profile
enforcement, and binds the backend to `127.0.0.1`. Gate's online authentication
must remain enabled for preparation. Lite preparation restores the original
network settings saved before Classic setup. Other property edits are preserved.
Prior property files and the original network settings remain with the instance
and are included in backups and exports. Servers assigned to proxies using
conflicting modes cannot be prepared together. Configure external backends on
their own hosts.

Vanilla requires forwarding **None** in Classic mode. Its offline player UUIDs
differ from authenticated UUIDs, so existing inventories and permissions may need
migration when changing authentication modes. Preparation preserves world files
and does not migrate player data. Compatible modded/Paper backends need their
chosen forwarding mechanism configured separately.

The UI reports detected authentication mismatches, and start validation rejects
them before launching Gate. Stop Gate before changing features that alter its
memory reservation. Lite reserves 256 MiB. Classic adds 512 MiB for Via and 768 MiB
for managed Bedrock, giving 1536 MiB with both enabled. These reservations include
child processes and participate in host admission. Startup checks both the Gate
API and its Minecraft listener.

## Verification on 2026-09-05

- All 349 API and 141 frontend tests passed, with type checking, lint, and the
  installed production build.
- A real Gate 0.73.0 process with Via and managed Bedrock failed startup under the
  previous 256 MiB limit. The same configuration started under 1536 MiB.
- A real Classic connection reproduced the online-backend rejection. After
  preparation, isolated Classic and Lite proxies both delivered the Minecraft
  26.2 world-join packet. The test proxies used offline authentication to avoid
  requiring a player account; the installed Classic proxy retained online mode.
- The installed Lite preparation path restored backend authentication and
  forwarded its encryption challenge. The installation was returned to Classic.
- Evidence is retained in `/var/tmp/mcpanel-gate-login-xvh4bkpb`,
  `/var/tmp/mcpanel-gate-login-2r5lwm02`, and the `mcpanel-gate-classic-world-probe`
  systemd journal. Suite logs use `/var/tmp/mcpanel-gate-full-{api,web}.log`.

Upstream references: [Gate Lite](https://gate.minekube.com/guide/lite) and
[Gate configuration](https://gate.minekube.com/guide/config/).

# Gate versions and backend setup

Choose a stable Gate version when creating a proxy, or use the release picker on
its Gate settings page. Version changes run as jobs in Activity and retain the
previous binary for rollback. Updating a running proxy restarts it.

## Gate RAM

Stop Gate, open **Gate settings**, and edit **RAM limit (MiB)**. Save the setting,
then start Gate. The limit covers Gate and its child processes, is enforced by
the runtime, and participates in host memory admission. It is retained when
editing backends or changing proxy modes.

The minimum is 256 MiB. Classic adds a minimum of 512 MiB when Via is enabled and
768 MiB when managed Bedrock is enabled, for 1536 MiB with both. Lite ignores
saved Classic features. Choose the total allocation in steps of 64 MiB; the
panel rejects values above the host allocation ceiling.

## Manual backend configuration

MC Panel does not prepare or modify backend settings for Gate. To add a backend
or change proxy modes:

1. Choose the servers on **Backends**, then choose the mode and forwarding
   settings on **Gate settings**.
2. Read **Backend compatibility**. Each row shows the configuration file,
   setting, current value, expected value, and any required manual change.
3. Stop the affected servers and edit their configuration files manually.
4. Save Gate settings, restart the backends, and use **Check again**.
5. Start Gate and join using its advertised address and port.

Classic requires `online-mode=false` on Minecraft backends. Legacy, BungeeGuard,
and None forwarding also require `enforce-secure-profile=false`. Restrict direct
access to offline backends using a loopback bind or firewall. Vanilla requires
Classic forwarding **None**. Its offline UUIDs can differ from authenticated
UUIDs, so review existing inventories and permissions when changing modes.

Lite leaves authentication to each backend. Normally use `online-mode=true` and
disable backend requirements for Velocity, BungeeCord, and PROXY protocol.
Saved Classic forwarding options are inactive in Lite.

## Compatibility results

The compact comparison shows only settings that need changes or manual
verification. Matching and informational rows are hidden, and backends with
no issues appear only in the summary. Edit saved files manually and restart
affected backends. A running backend alone does not trigger **Review required**.

Paper checks `spigot.yml` and `config/paper-global.yml`, or `paper.yml` on older
versions, for forwarding support, proxy authentication, the Velocity secret, and
PROXY protocol. BungeeGuard checks `plugins/BungeeGuard/config.yml` for Gate's
allowed token. Secret rows show whether values match without displaying secrets.
External servers and unsupported mod configurations show expected values and
require manual verification. Configuration files do not prove plugins are loaded.

A stopped Gate can save settings while you configure its backends manually.
Detected problems block startup and changes to a running Gate. Failed live
checks retain the existing Gate configuration. Existing backend network backups
from earlier panel versions are preserved; restore any values manually as needed.

## Earlier verification on 2026-09-05

These checks predate removal of automatic backend preparation.

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
[Gate configuration](https://gate.minekube.com/guide/config/). Paper forwarding
settings follow [PaperMC's forwarding guide](https://docs.papermc.io/velocity/player-information-forwarding/).

# Debian/Ubuntu operations

These scripts install MC Panel as two conventional systemd services: the web
panel and a persistent Java runtime companion. They do not install Docker, Java, Node.js, npm, or a .NET
runtime on the target host, and the running service never invokes `sudo`.

## Host and Java prerequisites

Supported deployment hosts are current Debian or Ubuntu systemd machines on
`x86_64` (`linux-x64`) or `aarch64` (`linux-arm64`). Size memory for the sum of
configured JVM RAM plus MC Panel's hidden native-memory reserves, and size storage for worlds, logs, staging
downloads, and backups.
Each managed server has a 512 MiB minimum RAM setting, adjustable in 512 MiB
steps. The selected value is applied equally to Xms and Xmx. Each server also
receives a separate, larger cgroup v2 limit so native JVM memory and charged
cache fit without exposing a second setting.

Install suitable 64-bit Java runtimes using your normal host-management
process. MC Panel only discovers and probes existing executables with
`-XshowSettings:properties -version`; it never installs or updates Java.

Discovery checks:

- `JAVA_HOME/bin/java` and the service `PATH`
- `/usr/bin/java` (including the system alternatives selection)
- Java homes directly below `/usr/lib/jvm`
- Java homes one or two levels below `/opt`
- An absolute custom executable submitted and validated in the Java/System UI

The hardened unit hides `/home`, so per-user JVMs there are intentionally not
usable. Put additional system runtimes under `/usr/lib/jvm` or `/opt` and make
them executable by `mcpanel`. Install every major needed by the versions you
intend to run; several majors can coexist.

MC Panel reads the required Java major from official Mojang version metadata
and blocks an incompatible selection. As a current Paper-specific reference,
the [official Paper requirements](https://docs.papermc.io/paper/getting-started/)
recommend Java 8 for 1.7.10–1.11, Java 11 for 1.12–1.16.4, Java 16 for 1.16.5,
Java 17 for 1.17–1.19, Java 21 for 1.20–1.21.11, and Java 25 for 26.1 and later.
Vanilla, Fabric, Forge, and NeoForge requirements come from the selected
Minecraft version's metadata; legacy Forge releases that require Java 8 are
kept on Java 8 exactly. These may differ from old Paper releases. Recheck provider documentation
when adding a new release family.

## Publish

On a development/build machine, install the .NET 10 SDK and Node.js 22 or newer.
Create a clean self-contained directory as a regular user:

```bash
./deploy/publish.sh linux-x64 ./artifacts/mcpanel-linux-x64
# ARM64 target:
./deploy/publish.sh linux-arm64 ./artifacts/mcpanel-linux-arm64
```

The wrapper runs `npm ci`, builds the React client, and publishes
`McPanel.Api` with its .NET runtime and web assets. It refuses root execution,
unsupported RIDs, and an existing output directory. This follows Microsoft's
[self-contained publish model](https://learn.microsoft.com/dotnet/core/tools/dotnet-publish).

Review and transfer that directory through a trusted channel. The privileged
installer accepts a directory only, rejects symbolic links, and normalizes its
ownership/permissions.

## Install

Run the installer from a checkout containing both `install.sh` and
`mcpanel.service.in`:

```bash
sudo ./deploy/install.sh \
  --listen-address 192.168.1.20 \
  --port 8080 \
  ./artifacts/mcpanel-linux-x64
```

Use `sudo ./deploy/install.sh --help` for parameterized install, configuration,
data, and service paths. Paths must be absolute, non-overlapping, and made from
conservative filesystem characters.

The default bind is `0.0.0.0:8080`, which listens on every host interface.
Prefer a specific private-LAN address. The installer does **not** change a host
firewall or router:

- permit the panel port only from trusted administrator devices/subnets;
- permit each Minecraft game port only from its intended players;
- do not create a public NAT/port-forward for the panel;
- public exposure of the built-in HTTP endpoint is unsupported.

The installer creates:

- the non-login `mcpanel:mcpanel` system account;
- root-owned binaries under `/opt/mcpanel`;
- root-owned `/etc/mcpanel/mcpanel.env` and `setup-token`, both mode `0600`;
- `mcpanel`-owned state below `/var/lib/mcpanel`;
- hardened, enabled `mcpanel.service` and `mcpanel-runtime.service` units.

The setup token is printed once and remains available to root with:

```bash
sudo cat /etc/mcpanel/setup-token
```

Use it in the first-run screen to create the one administrator. After that
account exists, the setup endpoint is permanently disabled: the root-only token
may remain on disk but is ignored and cannot reset the account.

## Service and configuration

Common operations:

```bash
sudo systemctl status mcpanel
sudo systemctl status mcpanel-runtime
sudo systemctl restart mcpanel
sudo systemctl stop mcpanel
sudo systemctl start mcpanel
sudo journalctl -u mcpanel --since today
sudo journalctl -u mcpanel -f
sudo journalctl -u mcpanel-runtime -f
```

Edit `/etc/mcpanel/mcpanel.env` with `sudoedit`, retain root ownership and mode
`0600`, then restart the service. Installed deployment variables are:

| Variable | Default installed value | Purpose |
| --- | --- | --- |
| `ASPNETCORE_URLS` | `http://0.0.0.0:8080` | HTTP bind URL |
| `ASPNETCORE_ENVIRONMENT` | `Production` | ASP.NET Core environment |
| `MCPANEL_DATA_DIR` | `/var/lib/mcpanel` | Writable application and instance state |
| `MCPANEL_CONFIG_DIR` | `/etc/mcpanel` | Root-managed configuration location |
| `MCPANEL_SETUP_TOKEN` | random 64-digit hex value | First-admin setup only |

Do not put shell expressions or commands in this file. The file is consumed by
systemd as an environment file, not a shell script. `ASPNETCORE_URLS` may be
changed in place. Do not change the data or configuration paths without also
rendering a matching unit, creating the paths with the documented ownership,
and repeating those custom paths for future update/uninstall operations; using
the installer's path options on a fresh installation is safer.

Both units use `ProtectSystem=strict`, an empty capability set,
`NoNewPrivileges`, private temporary/devices views, restricted address families,
and `KillMode=mixed`. Only the configured data tree is writable. The runtime
unit owns Java and delegates only the systemd memory controller so it can create
an enforced sub-cgroup for each server. Stopping `mcpanel.service` preserves
servers by default; the Panel settings switch can instead request graceful
stops. Explicitly stopping `mcpanel-runtime.service`, uninstalling, or shutting
down the host gracefully stops every server before systemd enforces its timeout.
A production start is rejected if cgroup v2 memory delegation is unavailable.

## Backups and recovery

Use the per-server Backups page or a scheduled Backup action for routine world
backups. For a running server, MC Panel quiesces saves with `save-off` and
`save-all flush`, resumes with `save-on` even if staging fails, and compresses
the staged copy. A restore requires the server to be stopped and first creates
a safety backup.

Panel-created archives are below `/var/lib/mcpanel/backups`. Because they are
normally on the same host/disk as the worlds, copy important backups to
separate protected storage and test restores.

For complete disaster recovery, stop the service and take a permissions-
preserving offline copy of both `/etc/mcpanel` and `/var/lib/mcpanel`. The first
contains the service secret/configuration; the second contains SQLite state,
Data Protection keys, console history, worlds, instance files, logs, and panel
backups. Start the service only after the copy finishes. On a replacement host,
install the same or newer trusted build, stop it, restore both trees, correct
the data-tree ownership to `mcpanel:mcpanel`, and then start the service.

## Update and rollback

Build a fresh artifact in a new directory, then run:

```bash
sudo ./deploy/update.sh ./artifacts/mcpanel-linux-x64-new
```

The updater stages and validates the artifact before stopping only the panel service. The
runtime and active Minecraft servers remain online, and the panel reconnects
after the binary swap. When the runtime is idle it automatically reloads the
updated executable. The updater
does not modify `/etc/mcpanel` or `/var/lib/mcpanel`. The former binary tree is
retained beside `/opt/mcpanel` with a dated `.rollback-...` suffix. If an active
service cannot remain active for three consecutive checks, the old tree is
restored automatically and the failed build is retained with a `.failed-...`
suffix.

After validating the new build and taking any required external backup, a root
operator may remove old dated binary directories manually. Never remove the
active `/opt/mcpanel` directory or either state/configuration tree as part of
binary cleanup.

## Uninstall

The safe default removes the active binaries and unit while preserving all
configuration/data and the service account:

```bash
sudo ./deploy/uninstall.sh
```

This is suitable before a later reinstall and keeps numeric file ownership
stable. It also leaves dated update rollback directories for explicit operator
review.

A permanent purge requires both flags and deletes every managed instance,
world, database, key, log, and panel backup:

```bash
sudo ./deploy/uninstall.sh --purge --yes-really-purge
```

Make and verify an external backup before purging. Custom paths used during
installation must be repeated with matching `--install-dir`, `--config-dir`,
`--data-dir`, and `--service-name` options.

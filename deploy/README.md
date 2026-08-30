# Debian and Ubuntu operations

The global `mcpanel` command downloads MC Panel and manages its two systemd
services. Run it as a regular user. It asks sudo for access only when it
changes protected system files. Automation still works with cached or
passwordless sudo.

## Host setup

MC Panel supports x86-64 and ARM64 Debian or Ubuntu hosts with systemd 247 or newer and
cgroup v2. Release installs need `curl`, GNU `tar`, and `sha256sum`. Source
builds also need the .NET 10 SDK and Node.js 22 or newer.

Install each 64-bit Java major needed by your Minecraft servers. MC Panel
checks Mojang metadata before launch and rejects an incompatible runtime. It
looks in these locations:

- `JAVA_HOME/bin/java` and the service `PATH`
- `/usr/bin/java`
- Java homes directly below `/usr/lib/jvm`
- Java homes one or two levels below `/opt`
- An absolute path added through the Java page

The systemd unit hides `/home`, so it cannot use Java installed inside a user
home directory. Put extra runtimes under `/usr/lib/jvm` or `/opt`.

MC Panel assigns the selected memory value to both Xms and Xmx. It also creates
a larger cgroup limit for JVM native memory and charged file cache. Production
startup fails if the host cannot enforce that limit.

## Install and build

Run the interactive installer for the rolling `main` release:

```bash
curl -fsSL https://github.com/karilaa-dev/mc-panel/releases/download/main/install \
  | bash -s -- --listen-address 192.168.1.20 --port 6050
```

The bootstrap verifies the released manager before it runs. The wizard checks
the host, shows the fixed system paths, confirms the network settings, and
installs `/usr/local/bin/mcpanel`. GitHub is the default source and `main` is
the default release. Run either command below to update that rolling build:

```bash
mcpanel update
mcpanel update --release main
```

A future versioned release can use the same artifact contract:

```bash
mcpanel update --release v1.2.3
```

The default is `0.0.0.0:6050`. Restrict the panel port with the host firewall
and never forward it from a router. The installer does not change firewall,
DNS, or router settings.

To build a self-contained directory without installing it:

```bash
./mcpanel.sh build ./artifacts/mcpanel-linux-x64
./mcpanel.sh build --rid linux-arm64 ./artifacts/mcpanel-linux-arm64
```

The output directory must not already exist. Run `./mcpanel.sh help` in the
checkout for build options. Run `mcpanel help` for system-management options.

To build the current checkout and apply it to the system installation, bypass
GitHub explicitly:

```bash
./mcpanel.sh update --source local
```

## Services and files

The installer creates `mcpanel.service` for the web application and
`mcpanel-runtime.service` for Minecraft and Gate processes. Both run as the
non-login `mcpanel` user.

```bash
mcpanel status
sudo systemctl status mcpanel mcpanel-runtime
sudo systemctl restart mcpanel
sudo journalctl -u mcpanel -f
sudo journalctl -u mcpanel-runtime -f
```

Stopping only `mcpanel.service` keeps managed servers running by default. The
panel setting named `Keep servers running when panel stops` controls this.
Stopping `mcpanel-runtime.service` stops every managed process.

The default paths are:

| Path | Owner | Contents |
| --- | --- | --- |
| `/usr/local/bin/mcpanel` | root | Global system-management command |
| `/opt/mcpanel` | root | Read-only application files |
| `/etc/mcpanel` | root | Readable, non-secret environment configuration |
| `/etc/credstore/mcpanel.setup-token` | root | Root-only setup credential loaded by systemd |
| `/var/lib/mcpanel` | `mcpanel` | Private panel state and managed servers |

Edit `/etc/mcpanel/mcpanel.env` with `sudoedit`, then restart the panel. Keep
the file owned by root with mode `0644`. Its installed values set the HTTP URL,
data directory, configuration directory, and environment; it contains no
secrets. `/etc/mcpanel` is root-owned mode `0755`.

Install and update add the invoking account to the `mcpanel` group. Sign out
and back in before using the new membership. Regular instance directories are
setgid and group-readable and writable, so that account can work in
`/var/lib/mcpanel/instances/<id>` without root. Gate instance trees remain
owner-only. The instances parent is mode `2750`, so group members cannot
rename, delete, or replace its child directories. Databases, keys, backups,
staging, logs, Modrinth baselines, and runtime internals remain private below
the mode-`0750` data root.

All Minecraft servers, plugins, mods, and Gate proxies run under the same Unix
account. They cannot normally write outside `/var/lib/mcpanel`, but they can
read or change sibling instances. Do not mix extensions from mutually
untrusted owners on one installation.

## Import an existing Minecraft server

Run imports as the same regular user used for installation. The wrapper asks
sudo for access before it copies the source into protected staging and pauses
the web service during the final commit.

```bash
mcpanel import-server /srv/minecraft/old-server
mcpanel import-server /srv/minecraft/old-server.zip --dry-run
```

The source may be an unpacked directory, `.zip`, `.tar`, `.tar.gz`, or `.tgz`.
It must be the exact server root and contain `server.properties`. Archives with
a containing directory are rejected. The importer also rejects links, special
files, duplicate archive paths, absolute paths, and parent-directory paths.

The wizard asks for the server kind, version, launch target, Java runtime,
heap size, port, optional JVM arguments, and EULA acceptance. It does not read
or execute old start scripts. For scripts and configuration management, pass
the matching flags with `--non-interactive`; add `--json` for a single JSON
result. Run `mcpanel help` for the complete option list.

The source remains unchanged. MC Panel writes the selected port and EULA only
to the managed copy, then registers it as stopped with start-on-boot disabled.
It does not import an old panel database, schedules, backup records, console
history, or Gate configuration.
The wrapper stops and restarts `mcpanel.service` only if it was active. It
never stops `mcpanel-runtime.service`, so existing managed servers remain
online. Repeat custom installation path and service-name options on the import
command.

## Backups and recovery

The Backups page can snapshot and restore one server. MC Panel pauses world
saves while it copies a running server. A restore requires the server to be
stopped and creates a safety backup first.

Panel backups normally live on the same disk as the worlds. Copy important
backups elsewhere and test them.

For a full offline backup, stop both services and copy `/etc/mcpanel`,
`/etc/credstore/mcpanel.setup-token`, and `/var/lib/mcpanel` while preserving
permissions. The first directory contains non-secret configuration, the
credential file contains the setup secret, and the data directory contains databases, keys, instances,
worlds, Gate files, logs, and panel backups.

To recover on another host, install the same or a newer trusted revision, stop
both services, restore both directories, and set the data tree owner to
`mcpanel:mcpanel` before starting the services.

## Updates and removal

```bash
mcpanel update
```

An update downloads the newest commit from the selected release. A same-version
run refreshes the credential, unit files, group membership, and access
permissions. Otherwise, it replaces the web application while the runtime keeps active servers online.
The updater retains the previous application beside `/opt/mcpanel` in a dated
rollback directory. It restores that copy if the new panel does not stay
active. Updates preserve application configuration, credentials, and data.

The normal uninstall removes the global command, application, and services. It
keeps all state and the service account:

```bash
mcpanel uninstall
```

It also preserves the setup credential. Purge removes the credential together
with configuration and data.

Permanent removal requires an explicit confirmation flag:

```bash
mcpanel purge --yes-really-purge
```

Purge deletes every managed server, world, database, key, log, and panel
backup. Repeat any custom path and service-name options used during install.

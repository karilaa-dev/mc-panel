# Debian and Ubuntu operations

`mcpanel.sh` downloads MC Panel and manages its two systemd services. Run it as
a regular user. The script uses passwordless `sudo` only when it changes the
system installation.

## Host setup

MC Panel supports x86-64 and ARM64 Debian or Ubuntu hosts with systemd and
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

Download the installer, then install the rolling `main` release:

```bash
curl --fail --location --show-error \
  --output mcpanel.sh \
  https://raw.githubusercontent.com/karilaa-dev/mc-panel/main/mcpanel.sh
chmod +x mcpanel.sh
./mcpanel.sh install --listen-address 192.168.1.20 --port 6050
```

The saved script refreshes itself from the selected release before it installs
the matching application artifact. GitHub is the default source and `main` is
the default release. Both forms below select the same rolling build:

```bash
./mcpanel.sh update
./mcpanel.sh update --source github --release main
```

A future versioned release can use the same artifact contract:

```bash
./mcpanel.sh update --release v1.2.3
```

The default is `0.0.0.0:6050`. Restrict the panel port with the host firewall
and never forward it from a router. The installer does not change firewall,
DNS, or router settings.

To build a self-contained directory without installing it:

```bash
./mcpanel.sh build ./artifacts/mcpanel-linux-x64
./mcpanel.sh build --rid linux-arm64 ./artifacts/mcpanel-linux-arm64
```

The output directory must not already exist. Run `./mcpanel.sh help` for custom
installation paths and service names.

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
./mcpanel.sh status
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
| `/opt/mcpanel` | root | Read-only application files |
| `/etc/mcpanel` | root | Environment file and setup token |
| `/var/lib/mcpanel` | `mcpanel` | Panel state and managed servers |

Edit `/etc/mcpanel/mcpanel.env` with `sudoedit`, then restart the panel. Keep
the file owned by root with mode `0600`. Its installed values set the HTTP URL,
data directory, configuration directory, environment, and initial setup token.

All Minecraft servers, plugins, mods, and Gate proxies run under the same Unix
account. They cannot normally write outside `/var/lib/mcpanel`, but they can
read or change sibling instances. Do not mix extensions from mutually
untrusted owners on one installation.

## Backups and recovery

The Backups page can snapshot and restore one server. MC Panel pauses world
saves while it copies a running server. A restore requires the server to be
stopped and creates a safety backup first.

Panel backups normally live on the same disk as the worlds. Copy important
backups elsewhere and test them.

For a full offline backup, stop both services and copy `/etc/mcpanel` and
`/var/lib/mcpanel` while preserving permissions. The first directory contains
configuration and secrets. The second contains databases, keys, instances,
worlds, Gate files, logs, and panel backups.

To recover on another host, install the same or a newer trusted revision, stop
both services, restore both directories, and set the data tree owner to
`mcpanel:mcpanel` before starting the services.

## Updates and removal

```bash
./mcpanel.sh update
```

An update downloads the newest commit from the selected release. It exits
without restarting services when that commit is already installed. Otherwise,
it replaces the web application while the runtime keeps active servers online.
The updater retains the previous application beside `/opt/mcpanel` in a dated
rollback directory. It restores that copy if the new panel does not stay
active. Updates do not replace `/etc/mcpanel` or `/var/lib/mcpanel`.

The normal uninstall keeps all state and the service account:

```bash
./mcpanel.sh uninstall
```

Permanent removal requires an explicit confirmation flag:

```bash
./mcpanel.sh purge --yes-really-purge
```

Purge deletes every managed server, world, database, key, log, and panel
backup. Repeat any custom path and service-name options used during install.

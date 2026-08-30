# MC Panel

MC Panel manages Minecraft servers from a web browser. It supports Vanilla,
Paper, Fabric, Forge, NeoForge, custom executable JARs, Modrinth packs, and Minekube Gate proxies. The
panel handles installs, Java selection, console access, files, players,
backups, schedules, mods, and plugins.

It runs Java processes directly under systemd. Docker is not required. MC
Panel finds Java runtimes already installed on the host but does not install
Java itself.

> MC Panel is for a trusted private network. Its built-in web server uses
> HTTP. Do not expose the panel port to the Internet. Read
> [SECURITY.md](SECURITY.md) before installing it.

## Requirements

- Debian or Ubuntu with systemd 247 or newer and cgroup v2
- An x86-64 or ARM64 processor
- `curl`, GNU `tar`, and `sha256sum`
- `sudo` access for system installation
- A 64-bit Java runtime supported by each Minecraft version you plan to run

The default installer downloads a self-contained application from GitHub. The
.NET 10 SDK and Node.js 22 or newer are needed only for source builds.

## Install

Run the setup wizard as your regular user:

```bash
curl -fsSL https://github.com/karilaa-dev/mc-panel/releases/download/main/install | bash
```

The wizard checks the host, asks for the listen address and port, and shows a
summary before it asks for sudo access. It downloads the newest successful
build from `main`, verifies the manager and application against the release
manifest, and installs the global `mcpanel` command. Running the one-line
installer again offers to update an existing default installation.

Pass setup options after `bash -s --`. For example, select a release tag or
bind to one private address:

```bash
curl -fsSL https://github.com/karilaa-dev/mc-panel/releases/download/main/install \
  | bash -s -- --release v1.2.3 --listen-address 192.168.1.20 --port 6050
```

The default address is `http://0.0.0.0:6050`, which listens on every network
interface. Keep that port on a trusted private network.

The installer prints a setup token. Open the panel from another device on the
same network and use that token to create the administrator account. Root can
read the token again before setup:

```bash
sudo cat /etc/credstore/mcpanel.setup-token
```

The token is stored as a protected systemd credential rather than in the
environment file. The panel has one administrator account. The setup token stops working after
that account exists.

The installer adds the invoking account to the `mcpanel` group. Sign out and
back in once after install or update. That group membership lets the regular
account read and edit regular server instance files without opening panel
databases, backups, Gate instances, keys, or runtime state.

When creating a server, choose `Regular server` or `Proxy`. Regular servers can
use verified official software, a Modrinth pack, or an uploaded executable
Custom JAR. A regular server also has a Software page for changing its core,
Minecraft version, loader or build, Java runtime, or launch JAR while stopped.

## Import an existing server

Import an unpacked Minecraft server directory with the interactive wizard:

```bash
mcpanel import-server /srv/old-survival
```

The command also accepts `.zip`, `.tar`, `.tar.gz`, and `.tgz` archives. An
archive must contain `server.properties` and the server files at its root. An
archive that contains one outer directory is rejected; extract it and pass
that directory instead.

For an unattended import, provide every value that cannot come from
`server.properties`:

```bash
mcpanel import-server /srv/old-survival.tar.gz \
  --name "Survival" \
  --kind paper \
  --version 1.21.8 \
  --launch-target paper.jar \
  --java-runtime /usr/bin/java \
  --memory-mb 4096 \
  --accept-eula \
  --non-interactive
```

Fabric, Forge, and NeoForge imports also require `--loader-version`. Use
`--port` to override `server-port`, and `--jvm-args` for extra JVM arguments.
Add `--dry-run` to inspect and validate without registering the server. Add
`--json` for one machine-readable result; it also enables non-interactive
mode.

The importer copies the source and never changes or removes it. The managed
copy starts in the stopped state with start-on-boot disabled. A real import
briefly stops the web panel while it commits the new server, but the persistent
runtime and existing Minecraft servers stay online. Old panel databases,
schedules, backup records, console history, and Gate configuration are not
restored.

## Maintenance

```bash
mcpanel update
mcpanel status
mcpanel uninstall
```

`update` downloads the newest build from the selected release and replaces the
installed application and the global manager command. When that commit is
already installed, it refreshes the manager, credentials, group membership,
permissions, and systemd units. Running Minecraft servers stay online during
a normal panel update.

Developers can build and install the current checkout instead:

```bash
./mcpanel.sh update --source local
```

`uninstall` removes the services and application but keeps configuration,
worlds, databases, and backups. `purge --yes-really-purge` deletes those files
too. Back them up first.

The default installation paths are:

| Path | Contents |
| --- | --- |
| `/usr/local/bin/mcpanel` | Global system-management command |
| `/opt/mcpanel` | Application files |
| `/etc/mcpanel` | Root-owned, generally readable service configuration (no secrets) |
| `/etc/credstore/mcpanel.setup-token` | Root-only systemd setup credential |
| `/var/lib/mcpanel` | Databases, server instances, logs, and backups |

See [deploy/README.md](deploy/README.md) for Java discovery, service commands,
updates, backups, and recovery.

## Local development

Start a repo-local instance on port 8080:

```bash
./start-local.sh
```

The script prints the local-network URL and setup token. It stores its data in
`.mcpanel-local`. To erase only that development data and start again, run:

```bash
./reset-and-start-local.sh
```

Run the checks with:

```bash
npm ci --prefix src/McPanel.Web
npm run typecheck --prefix src/McPanel.Web
npm run lint --prefix src/McPanel.Web
npm test --prefix src/McPanel.Web
npm run build --prefix src/McPanel.Web
dotnet test McPanel.slnx --configuration Release
```

The API notes are in [src/McPanel.Api/CONTRACT.md](src/McPanel.Api/CONTRACT.md).

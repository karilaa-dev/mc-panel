# MC Panel

MC Panel manages Minecraft servers from a web browser. It supports Vanilla,
Paper, Fabric, Forge, NeoForge, Modrinth packs, and Minekube Gate proxies. The
panel handles installs, Java selection, console access, files, players,
backups, schedules, mods, and plugins.

It runs Java processes directly under systemd. Docker is not required. MC
Panel finds Java runtimes already installed on the host but does not install
Java itself.

> MC Panel is for a trusted private network. Its built-in web server uses
> HTTP. Do not expose the panel port to the Internet. Read
> [SECURITY.md](SECURITY.md) before installing it.

## Requirements

- Debian or Ubuntu with systemd and cgroup v2
- An x86-64 or ARM64 processor
- .NET 10 SDK
- Node.js 22 or newer with npm
- Passwordless `sudo` for the installer
- A 64-bit Java runtime supported by each Minecraft version you plan to run

The SDK and Node.js are needed to build from source. The installed panel is a
self-contained .NET application.

## Install

Run the installer as your regular user:

```bash
./mcpanel.sh install
```

The default address is `http://0.0.0.0:8080`, which listens on every network
interface. You can bind the panel to one private address instead:

```bash
./mcpanel.sh install --listen-address 192.168.1.20 --port 8080
```

The installer prints a setup token. Open the panel from another device on the
same network and use that token to create the administrator account. Root can
read the token again before setup:

```bash
sudo cat /etc/mcpanel/setup-token
```

The panel has one administrator account. The setup token stops working after
that account exists.

## Maintenance

```bash
./mcpanel.sh update
./mcpanel.sh status
./mcpanel.sh uninstall
```

`update` builds the current checkout and replaces the installed application.
Running Minecraft servers stay online during a normal panel update.

`uninstall` removes the services and application but keeps configuration,
worlds, databases, and backups. `purge --yes-really-purge` deletes those files
too. Back them up first.

The default installation paths are:

| Path | Contents |
| --- | --- |
| `/opt/mcpanel` | Application files |
| `/etc/mcpanel` | Service configuration and setup token |
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

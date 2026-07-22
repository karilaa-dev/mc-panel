# MC Panel

MC Panel is a focused Minecraft server management panel built with ASP.NET
Core/.NET 10 and React 19 with shadcn/ui. It provides the useful Minecraft
workflows of a general game-server panel without containers, multi-user
administration, or a dense operator interface.

> **Private-LAN software:** the built-in web endpoint is HTTP. Do not expose it
> directly to the Internet or forward its port from a router. Bind it to a
> trusted LAN address and enforce access with the host/network firewall. Read
> [SECURITY.md](SECURITY.md) before deployment.

## Capabilities

- Multiple Vanilla, Paper, Fabric, Forge, and NeoForge servers installed from
  verified upstream metadata and downloads.
- Direct supervised Java processes—no Docker daemon and no privileged runtime.
- Discovery and validation of installed Java runtimes, including multiple Java
  major versions on one host.
- Start, graceful stop, restart, crash recovery, resource metrics, and
  start-on-boot behavior.
- Persistent live console with reconnect cursors, search, command history, and
  separate stdout/stderr display.
- Synchronized Xms/Xmx controls, version-aware sectioned `server.properties`,
  cropped server icons, player actions, and a confined file manager.
- Read-only Fabric, Forge, and NeoForge mod inventories with metadata details
  parsed directly from each instance's top-level mod JARs.
- Per-server backups and time-based automations.
- One local administrator protected by cookie authentication, global session
  revocation, and antiforgery tokens.

There is intentionally no sleep mode, Docker integration, Java auto-installer,
multi-admin role system, or adoption of arbitrary existing server directories.

AMP is a CubeCoders product and trademark. MC Panel is an independent,
clean-room implementation informed by publicly documented behavior; it does
not include AMP code, assets, or branding.

## Layout and process model

The production service runs as the non-login `mcpanel` account. The panel
starts each Minecraft server directly with a probed Java executable and
redirected standard input/output; no shell, `sudo`, or container runtime is
involved.

| Path | Ownership and purpose |
| --- | --- |
| `/opt/mcpanel` | Root-owned, read-only published application |
| `/etc/mcpanel` | Root-owned service configuration and first-run secret |
| `/var/lib/mcpanel` | `mcpanel`-owned databases, keys, instances, staging, backups, and logs |
| `/etc/systemd/system/mcpanel.service` | Root-owned hardened systemd unit |

Every Minecraft server and every installed plugin/mod runs under the same Unix
UID. This protects the host from ordinary writes outside the data directory,
but it is **not isolation between instances**: malicious server extensions can
read or alter sibling instances. Install only trusted plugins and mods.

The HTTP/realtime contract is documented in
[src/McPanel.Api/CONTRACT.md](src/McPanel.Api/CONTRACT.md). Production
operations are covered in [deploy/README.md](deploy/README.md).

## Development

Prerequisites:

- .NET 10 SDK
- Node.js 22 or newer with npm
- One or more locally installed Java runtimes for exercising server startup

### Quick local-network start

On Linux, the included scripts build the web client and expose MC Panel on port
8080 to devices on the same local network:

```bash
./start-local.sh
```

The script prints the LAN URL and one-time setup token. Use that token in the
setup screen, then choose your own username and password. Subsequent normal
starts preserve the local database and use the same command.

To erase the repo-local development database and all other repo-local panel
data, then start with a fresh setup token and administrator account:

```bash
./reset-and-start-local.sh
```

The reset script only removes `.mcpanel-local` in this repository. It does not
touch a production installation under `/var/lib/mcpanel` or `/etc/mcpanel`.
Because the server listens on every network interface, use it only on a trusted
LAN and do not forward port 8080 from your router.

Install the locked frontend dependencies, build the web client, and run the
tests:

```bash
npm ci --prefix src/McPanel.Web
npm run build --prefix src/McPanel.Web
npm run lint --prefix src/McPanel.Web
npm test --prefix src/McPanel.Web
dotnet test McPanel.slnx --configuration Release
```

For a non-root local run, use temporary data/configuration rather than the
production paths. Building the frontend first lets ASP.NET Core serve the
bundled UI from the same origin:

```bash
MCPANEL_DEV_ROOT="$(mktemp -d /tmp/mcpanel-dev.XXXXXX)"
export MCPANEL_DATA_DIR="$MCPANEL_DEV_ROOT/data"
export MCPANEL_CONFIG_DIR="$MCPANEL_DEV_ROOT/config"
export MCPANEL_SETUP_TOKEN="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
export ASPNETCORE_URLS="http://127.0.0.1:8080"

npm ci --prefix src/McPanel.Web
npm run build --prefix src/McPanel.Web
dotnet run --project src/McPanel.Api/McPanel.Api.csproj
```

Open `http://127.0.0.1:8080` and use the value of
`MCPANEL_SETUP_TOKEN` to create the single administrator. The temporary
directory printed in `MCPANEL_DEV_ROOT` can be removed after the process has
stopped.

## Build and install

Create a complete self-contained artifact as a regular user. Choose the RID
that matches the Debian/Ubuntu host:

```bash
./deploy/publish.sh linux-x64 ./artifacts/mcpanel-linux-x64
# or: ./deploy/publish.sh linux-arm64 ./artifacts/mcpanel-linux-arm64
```

The target host does not need Node.js or a .NET runtime. It does need every Java
major required by the Minecraft versions it will run; MC Panel discovers Java
but never installs it.

Install the artifact as root. Binding to one LAN address is safer than the
all-interface default:

```bash
sudo ./deploy/install.sh \
  --listen-address 192.168.1.20 \
  --port 8080 \
  ./artifacts/mcpanel-linux-x64
```

The installer prints a random first-run token and stores root-only copies in
`/etc/mcpanel`. The token remains readable only by root and is permanently
ignored by the application after the first administrator is created.

See [deploy/README.md](deploy/README.md) for Java compatibility, firewall
responsibility, service operation, backup/restore, updates, rollback, and safe
uninstallation.

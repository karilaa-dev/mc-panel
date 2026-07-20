# Security policy and deployment model

MC Panel is designed for a trusted private LAN and one trusted administrator.
The default deployment is deliberately small: one unprivileged service account,
direct Java child processes, root-owned code/configuration, and one writable
state tree.

## Reporting a vulnerability

Do not disclose an unpatched vulnerability in a public issue. Use the
repository host's private security-advisory feature, or contact the repository
maintainer through a private channel listed by that host. Include the affected
revision, deployment shape, reproduction steps, impact, and any proposed
mitigation. Avoid including real setup tokens, cookies, worlds, or player data.

## Supported security boundary

- The web UI is served over HTTP and is supported only on a trusted LAN. The
  operator is responsible for interface binding, firewall policy, DNS, TLS
  termination if added, and router/NAT configuration.
- Direct Internet exposure of the panel is unsupported. Do not port-forward
  the panel port. Only intentionally public Minecraft game ports should be
  reachable from outside the LAN.
- `mcpanel` is a non-login user with no runtime `sudo` or Linux capabilities.
  Application files are root-owned and systemd mounts the host filesystem
  read-only for the service except for the configured data directory.
- Authentication has exactly one local administrator. It is not a tenant or
  role boundary. Changing the password or logging out rotates a persistent
  session stamp and invalidates every older authentication cookie. Password
  changes reissue the current browser's cookie so the administrator can
  continue working; copied and other older cookies are rejected. Existing
  realtime connections tied to the old stamp stop receiving panel broadcasts
  and are told to reauthenticate when the stamp rotates.
- The installer creates a random setup token in both the root-only
  `mcpanel.env` and `setup-token` files with mode `0600`. The application
  permanently ignores this token after the first administrator is created. It
  cannot be used to reset or replace that administrator.
- Data Protection keys, state, console history, worlds, and backups live below
  `/var/lib/mcpanel` by default and must remain readable only by the service
  account and trusted root operators.

## Plugins, mods, and instance isolation

All panel-managed Java processes use the same `mcpanel` UID. Consequently,
plugins and mods can access data belonging to other MC Panel instances and can
damage anything writable by that account. The systemd sandbox limits access to
the rest of the host, but it does not create a security boundary between
Minecraft servers.

Treat every plugin, mod, Fabric loader, Paper experimental build, and uploaded
executable as trusted code. If mutually untrusted instance owners or extensions
are required, this deployment model is unsuitable; use separate hosts or a
properly isolated platform.

## Operator responsibilities

- Keep the host OS, Java runtimes, MC Panel, server distributions, plugins, and
  mods patched. Obtain publish artifacts from a trusted source and verify them
  before invoking a root installer.
- Restrict TCP 8080 (or the configured panel port) to administrator devices.
  Restrict each Minecraft port according to who should join that server.
- Use a strong unique administrator password and protect browser sessions on
  shared devices.
- Protect offline copies of `/etc/mcpanel` and `/var/lib/mcpanel`; they contain
  secrets and complete worlds. Panel-created backups on the same disk are not
  disaster recovery.
- Review `journalctl -u mcpanel` and panel logs after unexpected restarts,
  checksum failures, repeated login failures, or unrecognized server commands.

## Suspected compromise

1. Block the panel and affected Minecraft ports at the firewall, then stop the
   service with `sudo systemctl stop mcpanel`.
2. Preserve the journal, `/etc/mcpanel`, and `/var/lib/mcpanel` for analysis.
   Do not launch suspect server JARs or extensions on another trusted host.
3. Reinstall a verified MC Panel artifact, replace suspect plugins/mods and
   server files, patch Java/the OS, and change the administrator password.
4. Restore worlds only from a backup known to predate the compromise. Reopen
   network access after reviewing all sibling instances, because they share a
   UID.

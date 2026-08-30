# Security policy

MC Panel is built for one trusted administrator on a private network. Its web
server uses HTTP. Do not expose the panel port to the Internet.

## Report a vulnerability

Use the repository host's private security advisory feature, or contact the
maintainer through a private channel listed there. Include the affected
revision, reproduction steps, and impact. Do not include real setup tokens,
cookies, worlds, or player data.

## Security boundaries

- The `mcpanel` service account has no login shell, `sudo`, or Linux
  capabilities. Systemd gives it write access only to the data directory.
- The panel supports one local administrator. It has no roles or tenant
  isolation. Password changes and logout invalidate older sessions.
- The setup token can create only the first administrator. It cannot reset an
  existing account.
- All managed servers and extensions share the `mcpanel` Unix account. A
  malicious plugin or mod can read or damage sibling instances.
- Gate management APIs bind to loopback. Gate game ports follow the firewall
  and router rules set by the operator.
- Configuration, browser sessions, worlds, backups, forwarding secrets, and
  player inventories contain sensitive data.

Use separate hosts or an isolated platform when server owners or extensions do
not trust one another.

## Operator checklist

- Restrict the panel port to administrator devices on the private network.
- Do not create a public port forward for the panel.
- Patch the host, Java runtimes, MC Panel, server software, plugins, and mods.
- Install plugins and mods only from sources you trust.
- Use a unique administrator password and protect logged-in browsers.
- Copy `/etc/mcpanel` and `/var/lib/mcpanel` to protected offline storage.
- Review the system journal and panel logs after unexpected activity.

## Suspected compromise

1. Block the panel and affected game ports at the firewall.
2. Stop both services with `sudo systemctl stop mcpanel mcpanel-runtime`.
3. Preserve `/etc/mcpanel`, `/var/lib/mcpanel`, and the system journal.
4. Reinstall a verified build and replace suspect server files and extensions.
5. Change the administrator password and rotate affected Gate secrets.
6. Restore worlds only from a backup made before the compromise.

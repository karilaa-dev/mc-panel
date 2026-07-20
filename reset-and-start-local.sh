#!/usr/bin/env bash
set -Eeuo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
local_root="$repo_dir/.mcpanel-local"

if [[ "$local_root" != "$repo_dir/.mcpanel-local" || "$local_root" == "/.mcpanel-local" ]]; then
  echo "Refusing to reset an unexpected path: $local_root" >&2
  exit 1
fi

# This deliberately removes only the repo-local development state. Production
# data under /var/lib/mcpanel and /etc/mcpanel is never touched.
if [[ -e "$local_root" ]]; then
  rm -rf -- "$local_root"
  echo "Cleared local MC Panel database and data: $local_root"
else
  echo "The local MC Panel database is already empty."
fi

exec "$repo_dir/start-local.sh"

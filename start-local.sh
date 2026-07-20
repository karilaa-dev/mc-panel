#!/usr/bin/env bash
set -Eeuo pipefail

repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
local_root="$repo_dir/.mcpanel-local"
data_dir="$local_root/data"
config_dir="$local_root/config"
setup_token_file="$config_dir/setup-token"

command -v dotnet >/dev/null 2>&1 || { echo "Error: .NET 10 SDK is required." >&2; exit 1; }
command -v npm >/dev/null 2>&1 || { echo "Error: Node.js and npm are required." >&2; exit 1; }

mkdir -p -- "$data_dir" "$config_dir"
if [[ ! -s "$setup_token_file" ]]; then
  umask 077
  od -An -N32 -tx1 /dev/urandom | tr -d ' \n' > "$setup_token_file"
  printf '\n' >> "$setup_token_file"
fi
setup_token="$(tr -d '\r\n' < "$setup_token_file")"

if [[ ! -d "$repo_dir/src/McPanel.Web/node_modules" ]]; then
  npm ci --prefix "$repo_dir/src/McPanel.Web"
fi
npm run build --prefix "$repo_dir/src/McPanel.Web"

lan_addresses="$(hostname -I 2>/dev/null | xargs || true)"
echo
echo "MC Panel is starting on port 8080 for devices on your local network."
echo "Setup token: $setup_token"
if [[ -n "$lan_addresses" ]]; then
  for address in $lan_addresses; do
    [[ "$address" == *:* ]] && continue
    echo "Open: http://$address:8080"
  done
else
  echo "Open: http://<this-computer's-LAN-IP>:8080"
fi
echo "Keep this on a trusted LAN; do not forward port 8080 from your router."
echo

export MCPANEL_DATA_DIR="$data_dir"
export MCPANEL_CONFIG_DIR="$config_dir"
export MCPANEL_SETUP_TOKEN_FILE="$setup_token_file"
export ASPNETCORE_URLS="http://0.0.0.0:8080"
export ASPNETCORE_ENVIRONMENT="Development"

exec dotnet run --project "$repo_dir/src/McPanel.Api/McPanel.Api.csproj" --no-launch-profile

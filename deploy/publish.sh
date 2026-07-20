#!/usr/bin/env bash

set -Eeuo pipefail

rid="${1:-}"
output_arg="${2:-}"
stage_dir=""

usage() {
  cat <<'EOF'
Usage: ./deploy/publish.sh linux-x64|linux-arm64 OUTPUT_DIRECTORY

Build the React client, then create a self-contained McPanel.Api publish
directory for a supported Debian/Ubuntu architecture. Run this as a regular
development user; only deploy/install.sh requires root.
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

cleanup() {
  if [[ -n "$stage_dir" && -d "$stage_dir" ]]; then
    rm -rf -- "$stage_dir"
  fi
}
trap cleanup EXIT

if [[ "$rid" == "-h" || "$rid" == "--help" ]]; then
  usage
  exit 0
fi
[[ $# -eq 2 ]] || { usage >&2; die "a runtime identifier and output directory are required"; }
case "$rid" in
  linux-x64|linux-arm64) ;;
  *) die "unsupported runtime identifier: $rid" ;;
esac
[[ "${EUID:-$(id -u)}" -ne 0 ]] || die "run publishing as a regular user, not root"

for command_name in dotnet mktemp mv npm realpath; do
  command -v "$command_name" >/dev/null 2>&1 || die "required command not found: $command_name"
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(realpath -e -- "$script_dir/..")"
web_project="$repo_root/src/McPanel.Web"
api_project="$repo_root/src/McPanel.Api/McPanel.Api.csproj"
[[ -f "$web_project/package-lock.json" ]] || die "frontend package-lock.json is missing"
[[ -f "$api_project" ]] || die "backend project is missing: $api_project"

if [[ "$output_arg" == /* ]]; then
  output_dir="$(realpath -m -- "$output_arg")"
else
  output_dir="$(realpath -m -- "$PWD/$output_arg")"
fi
[[ "$output_dir" != "/" && "$output_dir" != "$repo_root" ]] || die "refusing unsafe output directory"
[[ "$output_dir" != "$repo_root/src" && "$output_dir" != "$repo_root/src/"* ]] || \
  die "publish output must be outside the source tree"
[[ ! -e "$output_dir" ]] || die "output already exists: $output_dir"

output_parent="$(dirname -- "$output_dir")"
mkdir -p -- "$output_parent"
stage_dir="$(mktemp -d "$output_parent/.mcpanel-publish.XXXXXX")"

npm ci --prefix "$web_project"
npm run build --prefix "$web_project"
dotnet publish "$api_project" \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  --output "$stage_dir" \
  -p:DebugType=None \
  -p:DebugSymbols=false

[[ -f "$stage_dir/McPanel.Api" ]] || die "publish completed without the McPanel.Api executable"
chmod 0755 "$stage_dir/McPanel.Api"
mv -- "$stage_dir" "$output_dir"
stage_dir=""

printf 'Self-contained %s artifact: %s\n' "$rid" "$output_dir"

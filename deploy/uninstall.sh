#!/usr/bin/env bash

set -Eeuo pipefail

readonly DEFAULT_INSTALL_DIR="/opt/mcpanel"
readonly DEFAULT_CONFIG_DIR="/etc/mcpanel"
readonly DEFAULT_DATA_DIR="/var/lib/mcpanel"
readonly DEFAULT_SERVICE_NAME="mcpanel"
readonly PANEL_USER="mcpanel"
readonly PANEL_GROUP="mcpanel"

install_dir="$DEFAULT_INSTALL_DIR"
config_dir="$DEFAULT_CONFIG_DIR"
data_dir="$DEFAULT_DATA_DIR"
service_name="$DEFAULT_SERVICE_NAME"
purge=0
purge_confirmed=0

usage() {
  cat <<'EOF'
Usage: sudo ./deploy/uninstall.sh [options]

Disable the systemd service and remove the active MC Panel binaries. By
default, /etc/mcpanel, /var/lib/mcpanel, and the mcpanel account are preserved.

Options:
  --install-dir PATH        Binary directory (default: /opt/mcpanel)
  --config-dir PATH         Configuration directory (default: /etc/mcpanel)
  --data-dir PATH           Data directory (default: /var/lib/mcpanel)
  --service-name NAME       systemd unit name (default: mcpanel)
  --purge                   Also request deletion of configuration and data
  --yes-really-purge        Required together with --purge
  -h, --help                Show this help

Purge permanently deletes every managed instance, world, database, key, log,
and panel-created backup. It does not remove separately retained update
rollback directories beside /opt/mcpanel.
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

normalize_managed_path() {
  local raw="$1"
  local resolved

  [[ "$raw" == /* ]] || die "managed paths must be absolute: $raw"
  [[ "$raw" =~ ^/[A-Za-z0-9._/-]+$ ]] || die "path contains unsupported characters: $raw"
  resolved="$(realpath -m -- "$raw")"
  case "$resolved" in
    /|/bin|/boot|/dev|/etc|/home|/lib|/lib64|/opt|/proc|/root|/run|/sbin|/srv|/sys|/tmp|/usr|/var)
      die "refusing unsafe managed path: $resolved"
      ;;
  esac
  printf '%s\n' "$resolved"
}

while (($#)); do
  case "$1" in
    --install-dir)
      (($# >= 2)) || die "--install-dir requires a value"
      install_dir="$2"
      shift 2
      ;;
    --config-dir)
      (($# >= 2)) || die "--config-dir requires a value"
      config_dir="$2"
      shift 2
      ;;
    --data-dir)
      (($# >= 2)) || die "--data-dir requires a value"
      data_dir="$2"
      shift 2
      ;;
    --service-name)
      (($# >= 2)) || die "--service-name requires a value"
      service_name="$2"
      shift 2
      ;;
    --purge)
      purge=1
      shift
      ;;
    --yes-really-purge)
      purge_confirmed=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      die "unknown option: $1"
      ;;
  esac
done

[[ "${EUID:-$(id -u)}" -eq 0 ]] || die "run this uninstaller as root (for example, with sudo)"
[[ "$service_name" =~ ^[A-Za-z0-9_.@-]+$ ]] || die "invalid service name"
((!purge_confirmed || purge)) || die "--yes-really-purge is only valid with --purge"
((!purge || purge_confirmed)) || \
  die "purge was not confirmed; repeat with both --purge and --yes-really-purge"

for command_name in getent groupdel realpath rm rmdir systemctl userdel; do
  command -v "$command_name" >/dev/null 2>&1 || die "required command not found: $command_name"
done

install_dir="$(normalize_managed_path "$install_dir")"
config_dir="$(normalize_managed_path "$config_dir")"
data_dir="$(normalize_managed_path "$data_dir")"
for path_pair in \
  "$install_dir:$config_dir" \
  "$install_dir:$data_dir" \
  "$config_dir:$data_dir"; do
  first_path="${path_pair%%:*}"
  second_path="${path_pair#*:}"
  if [[ "$first_path" == "$second_path" || "$first_path" == "$second_path/"* || "$second_path" == "$first_path/"* ]]; then
    die "install, configuration, and data directories must not overlap"
  fi
done

for managed_path in "$install_dir" "$config_dir" "$data_dir"; do
  [[ ! -L "$managed_path" ]] || die "refusing to remove a symbolic-link managed path: $managed_path"
done
if [[ -e "$install_dir" ]]; then
  [[ -d "$install_dir" ]] || die "install path is not a directory: $install_dir"
  [[ -f "$install_dir/McPanel.Api" && ! -L "$install_dir/McPanel.Api" ]] || \
    die "refusing to remove a directory that does not contain a regular McPanel.Api executable"
fi

unit_file="/etc/systemd/system/$service_name.service"
runtime_service_name="$service_name-runtime"
runtime_unit_file="/etc/systemd/system/$runtime_service_name.service"
if [[ -e "$unit_file" ]]; then
  [[ -f "$unit_file" && ! -L "$unit_file" ]] || die "refusing to remove unsafe unit file: $unit_file"
fi
if [[ -e "$runtime_unit_file" ]]; then
  [[ -f "$runtime_unit_file" && ! -L "$runtime_unit_file" ]] || die "refusing to remove unsafe runtime unit file: $runtime_unit_file"
fi
systemctl disable --now "$service_name.service" >/dev/null 2>&1 || true
systemctl disable --now "$runtime_service_name.service" >/dev/null 2>&1 || true
if [[ -e "$unit_file" ]]; then
  rm -f -- "$unit_file"
fi
if [[ -e "$runtime_unit_file" ]]; then
  rm -f -- "$runtime_unit_file"
fi
memory_dropin_dir="/etc/systemd/system/$service_name.service.d"
memory_dropin="$memory_dropin_dir/50-mcpanel-memory.conf"
if [[ -e "$memory_dropin" ]]; then
  [[ -f "$memory_dropin" && ! -L "$memory_dropin" ]] || die "refusing to remove unsafe memory delegation drop-in: $memory_dropin"
  rm -f -- "$memory_dropin"
  rmdir --ignore-fail-on-non-empty -- "$memory_dropin_dir" 2>/dev/null || true
fi
systemctl daemon-reload
systemctl reset-failed "$service_name.service" >/dev/null 2>&1 || true
systemctl reset-failed "$runtime_service_name.service" >/dev/null 2>&1 || true

if [[ -d "$install_dir" ]]; then
  rm -rf --one-file-system -- "$install_dir"
fi

if ((purge)); then
  printf 'Permanently deleting %s and %s.\n' "$config_dir" "$data_dir"
  if [[ -d "$config_dir" ]]; then
    rm -rf --one-file-system -- "$config_dir"
  fi
  if [[ -d "$data_dir" ]]; then
    rm -rf --one-file-system -- "$data_dir"
  fi

  if getent passwd "$PANEL_USER" >/dev/null; then
    userdel "$PANEL_USER"
  fi
  if getent group "$PANEL_GROUP" >/dev/null; then
    groupdel "$PANEL_GROUP" 2>/dev/null || \
      printf 'warning: group %s is still in use and was retained.\n' "$PANEL_GROUP" >&2
  fi
  printf 'MC Panel binaries, configuration, and data were removed.\n'
else
  printf 'MC Panel binaries and systemd unit were removed.\n'
  printf 'Preserved configuration: %s\n' "$config_dir"
  printf 'Preserved instances, worlds, databases, keys, logs, and backups: %s\n' "$data_dir"
  printf 'The %s account was retained so preserved files keep a stable owner.\n' "$PANEL_USER"
fi

printf 'Review any dated rollback directories beside %s separately.\n' "$install_dir"

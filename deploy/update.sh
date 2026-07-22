#!/usr/bin/env bash

set -Eeuo pipefail

readonly DEFAULT_INSTALL_DIR="/opt/mcpanel"
readonly DEFAULT_CONFIG_DIR="/etc/mcpanel"
readonly DEFAULT_DATA_DIR="/var/lib/mcpanel"
readonly DEFAULT_SERVICE_NAME="mcpanel"

artifact_dir=""
install_dir="$DEFAULT_INSTALL_DIR"
config_dir="$DEFAULT_CONFIG_DIR"
data_dir="$DEFAULT_DATA_DIR"
service_name="$DEFAULT_SERVICE_NAME"
stage_dir=""
rollback_dir=""
was_active=0
old_unit_backup=""
old_runtime_unit_backup=""
old_memory_dropin_backup=""

usage() {
  cat <<'EOF'
Usage: sudo ./deploy/update.sh [options] PUBLISH_DIRECTORY

Replace MC Panel with a supplied self-contained publish directory. Panel
configuration, instances, databases, keys, logs, and backups are not touched.

Options:
  --install-dir PATH    Binary directory (default: /opt/mcpanel)
  --config-dir PATH     Configuration directory (default: /etc/mcpanel)
  --data-dir PATH       Data directory (default: /var/lib/mcpanel)
  --service-name NAME  systemd unit name (default: mcpanel)
  -h, --help            Show this help

The previous binary directory is retained beside the installation as a dated
rollback. If the service cannot remain active after an update, the script
restores the old directory and retains the failed build for inspection.
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

warn() {
  printf 'warning: %s\n' "$*" >&2
}

cleanup() {
  if [[ -n "$stage_dir" && -d "$stage_dir" ]]; then
    rm -rf -- "$stage_dir"
  fi
  for backup in "$old_unit_backup" "$old_runtime_unit_backup" "$old_memory_dropin_backup"; do
    if [[ -n "$backup" && -f "$backup" ]]; then rm -f -- "$backup"; fi
  done
}
trap cleanup EXIT

normalize_install_path() {
  local raw="$1"
  local resolved

  [[ "$raw" == /* ]] || die "install path must be absolute: $raw"
  [[ "$raw" =~ ^/[A-Za-z0-9._/-]+$ ]] || die "path contains unsupported characters: $raw"
  resolved="$(realpath -m -- "$raw")"
  case "$resolved" in
    /|/bin|/boot|/dev|/etc|/home|/lib|/lib64|/opt|/proc|/root|/run|/sbin|/srv|/sys|/tmp|/usr|/var)
      die "refusing unsafe install path: $resolved"
      ;;
  esac
  printf '%s\n' "$resolved"
}

rollback_update() {
  local failed_dir
  failed_dir="${install_dir}.failed-$(date -u +%Y%m%dT%H%M%SZ)-$$"

  warn "the updated service did not remain active; restoring $rollback_dir"
  systemctl stop "$service_name.service" >/dev/null 2>&1 || true

  if [[ -d "$install_dir" && ! -L "$install_dir" ]]; then
    mv -- "$install_dir" "$failed_dir"
  fi
  mv -- "$rollback_dir" "$install_dir"
  rollback_dir=""

  if [[ -n "$old_unit_backup" ]]; then cp -- "$old_unit_backup" "/etc/systemd/system/$service_name.service"; fi
  if [[ -n "$old_runtime_unit_backup" ]]; then
    cp -- "$old_runtime_unit_backup" "$runtime_unit"
  else
    systemctl disable --now "$runtime_service_name.service" >/dev/null 2>&1 || true
    if [[ -e "$runtime_unit" ]]; then rm -f -- "$runtime_unit"; fi
  fi
  if [[ -n "$old_memory_dropin_backup" ]]; then
    mkdir -p -- "$memory_dropin_dir"
    cp -- "$old_memory_dropin_backup" "$memory_dropin"
  elif [[ -e "$memory_dropin" ]]; then
    rm -f -- "$memory_dropin"
  fi
  systemctl daemon-reload

  if ((was_active)); then
    if ! systemctl start "$service_name.service"; then
      warn "the previous build was restored, but systemd could not restart it"
    fi
  fi

  die "update rolled back; failed files were retained at $failed_dir"
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
    -h|--help)
      usage
      exit 0
      ;;
    --*)
      die "unknown option: $1"
      ;;
    *)
      [[ -z "$artifact_dir" ]] || die "only one publish directory may be supplied"
      artifact_dir="$1"
      shift
      ;;
  esac
done

[[ "${EUID:-$(id -u)}" -eq 0 ]] || die "run this updater as root (for example, with sudo)"
[[ -n "$artifact_dir" ]] || { usage >&2; die "a publish directory is required"; }

for command_name in chmod chown cp date find grep mkdir mktemp mv realpath rm rmdir sed sleep systemctl; do
  command -v "$command_name" >/dev/null 2>&1 || die "required command not found: $command_name"
done

[[ "$service_name" =~ ^[A-Za-z0-9_.@-]+$ ]] || die "invalid service name"
install_dir="$(normalize_install_path "$install_dir")"
config_dir="$(normalize_install_path "$config_dir")"
data_dir="$(normalize_install_path "$data_dir")"
[[ -d "$install_dir" && ! -L "$install_dir" ]] || die "installation is missing or unsafe: $install_dir"
[[ -f "$install_dir/McPanel.Api" && ! -L "$install_dir/McPanel.Api" ]] || die "current McPanel.Api executable is missing"
[[ -f "/etc/systemd/system/$service_name.service" && ! -L "/etc/systemd/system/$service_name.service" ]] || \
  die "systemd unit is missing or unsafe: /etc/systemd/system/$service_name.service"
runtime_service_name="$service_name-runtime"
runtime_unit="/etc/systemd/system/$runtime_service_name.service"
memory_dropin_dir="/etc/systemd/system/$service_name.service.d"
memory_dropin="$memory_dropin_dir/50-mcpanel-memory.conf"
[[ ! -e "$memory_dropin" || -f "$memory_dropin" && ! -L "$memory_dropin" ]] || \
  die "memory delegation drop-in is unsafe: $memory_dropin"

artifact_dir="$(realpath -e -- "$artifact_dir")"
[[ -d "$artifact_dir" ]] || die "publish artifact is not a directory: $artifact_dir"
[[ -f "$artifact_dir/McPanel.Api" && ! -L "$artifact_dir/McPanel.Api" ]] || \
  die "publish directory does not contain a regular McPanel.Api executable"
[[ "$artifact_dir" != "$install_dir" && "$artifact_dir" != "$install_dir/"* ]] || \
  die "publish directory must be outside the active installation"

install_parent="$(dirname -- "$install_dir")"
stage_dir="$(mktemp -d "$install_parent/.mcpanel-update.XXXXXX")"
cp -a -- "$artifact_dir/." "$stage_dir/"
if find "$stage_dir" -type l -print -quit | grep -q .; then
  die "publish directory contains symbolic links; refusing a privileged update"
fi
if find "$stage_dir" ! -type d ! -type f -print -quit | grep -q .; then
  die "publish directory contains a device, socket, FIFO, or other special file"
fi
find "$stage_dir" -type d -exec chmod 0755 {} +
find "$stage_dir" -type f -exec chmod 0644 {} +
chmod 0755 "$stage_dir/McPanel.Api"
chown -R root:root "$stage_dir"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
service_template="$script_dir/mcpanel.service.in"
runtime_service_template="$script_dir/mcpanel-runtime.service.in"
[[ -f "$service_template" && ! -L "$service_template" ]] || die "service template is missing: $service_template"
[[ -f "$runtime_service_template" && ! -L "$runtime_service_template" ]] || die "runtime service template is missing: $runtime_service_template"

old_unit_backup="$(mktemp)"; cp -- "/etc/systemd/system/$service_name.service" "$old_unit_backup"
if [[ -e "$runtime_unit" ]]; then old_runtime_unit_backup="$(mktemp)"; cp -- "$runtime_unit" "$old_runtime_unit_backup"; fi
if [[ -e "$memory_dropin" ]]; then old_memory_dropin_backup="$(mktemp)"; cp -- "$memory_dropin" "$old_memory_dropin_backup"; fi

if systemctl is-active --quiet "$service_name.service"; then
  was_active=1
fi

rollback_dir="${install_dir}.rollback-$(date -u +%Y%m%dT%H%M%SZ)-$$"
[[ ! -e "$rollback_dir" ]] || die "rollback destination already exists: $rollback_dir"
systemctl stop "$service_name.service"
if ! mv -- "$install_dir" "$rollback_dir"; then
  if ((was_active)); then systemctl start "$service_name.service" || true; fi
  die "could not move the active binary directory; the old service was restarted"
fi
if ! mv -- "$stage_dir" "$install_dir"; then
  mv -- "$rollback_dir" "$install_dir" || \
    die "binary swap failed and $rollback_dir could not be restored; manual recovery is required"
  rollback_dir=""
  if ((was_active)); then systemctl start "$service_name.service" || true; fi
  die "could not activate the staged directory; the previous binaries were restored"
fi
stage_dir=""

if [[ -e "$memory_dropin" ]]; then rm -f -- "$memory_dropin"; fi
rmdir --ignore-fail-on-non-empty -- "$memory_dropin_dir" 2>/dev/null || true

unit_tmp="$(mktemp "/etc/systemd/system/.${service_name}.service.XXXXXX")"
sed -e "s|@INSTALL_DIR@|$install_dir|g" -e "s|@CONFIG_DIR@|$config_dir|g" -e "s|@DATA_DIR@|$data_dir|g" -e "s|@SERVICE_NAME@|$service_name|g" "$service_template" > "$unit_tmp"
chown root:root "$unit_tmp"; chmod 0644 "$unit_tmp"; mv -- "$unit_tmp" "/etc/systemd/system/$service_name.service"
runtime_unit_tmp="$(mktemp "/etc/systemd/system/.${runtime_service_name}.service.XXXXXX")"
sed -e "s|@INSTALL_DIR@|$install_dir|g" -e "s|@CONFIG_DIR@|$config_dir|g" -e "s|@DATA_DIR@|$data_dir|g" "$runtime_service_template" > "$runtime_unit_tmp"
chown root:root "$runtime_unit_tmp"; chmod 0644 "$runtime_unit_tmp"; mv -- "$runtime_unit_tmp" "$runtime_unit"
systemctl daemon-reload
systemctl enable --now "$runtime_service_name.service"

if ((was_active)); then
  if ! systemctl start "$service_name.service"; then
    rollback_update
  fi

  consecutive_active_checks=0
  for _ in {1..15}; do
    sleep 1
    if systemctl is-active --quiet "$service_name.service"; then
      ((consecutive_active_checks += 1))
      if ((consecutive_active_checks >= 3)); then
        break
      fi
    else
      consecutive_active_checks=0
    fi
  done

  if ((consecutive_active_checks < 3)); then
    rollback_update
  fi
fi

printf 'MC Panel binaries were updated successfully.\n'
if ((was_active)); then
  printf 'The service is active.\n'
else
  printf 'The service was not active before the update and was left stopped.\n'
fi
printf 'Previous binaries were retained at %s for manual rollback or removal.\n' "$rollback_dir"
printf 'Configuration and data were not modified.\n'

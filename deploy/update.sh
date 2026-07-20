#!/usr/bin/env bash

set -Eeuo pipefail

readonly DEFAULT_INSTALL_DIR="/opt/mcpanel"
readonly DEFAULT_SERVICE_NAME="mcpanel"

artifact_dir=""
install_dir="$DEFAULT_INSTALL_DIR"
service_name="$DEFAULT_SERVICE_NAME"
stage_dir=""
rollback_dir=""
was_active=0

usage() {
  cat <<'EOF'
Usage: sudo ./deploy/update.sh [options] PUBLISH_DIRECTORY

Replace MC Panel with a supplied self-contained publish directory. Panel
configuration, instances, databases, keys, logs, and backups are not touched.

Options:
  --install-dir PATH    Binary directory (default: /opt/mcpanel)
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

for command_name in chown cp date find grep mktemp mv realpath sleep systemctl; do
  command -v "$command_name" >/dev/null 2>&1 || die "required command not found: $command_name"
done

[[ "$service_name" =~ ^[A-Za-z0-9_.@-]+$ ]] || die "invalid service name"
install_dir="$(normalize_install_path "$install_dir")"
[[ -d "$install_dir" && ! -L "$install_dir" ]] || die "installation is missing or unsafe: $install_dir"
[[ -f "$install_dir/McPanel.Api" && ! -L "$install_dir/McPanel.Api" ]] || die "current McPanel.Api executable is missing"
[[ -f "/etc/systemd/system/$service_name.service" && ! -L "/etc/systemd/system/$service_name.service" ]] || \
  die "systemd unit is missing or unsafe: /etc/systemd/system/$service_name.service"

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

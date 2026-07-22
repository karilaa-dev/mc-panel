#!/usr/bin/env bash

set -Eeuo pipefail
umask 077

readonly DEFAULT_INSTALL_DIR="/opt/mcpanel"
readonly DEFAULT_CONFIG_DIR="/etc/mcpanel"
readonly DEFAULT_DATA_DIR="/var/lib/mcpanel"
readonly DEFAULT_SERVICE_NAME="mcpanel"
readonly PANEL_USER="mcpanel"
readonly PANEL_GROUP="mcpanel"

artifact_dir=""
install_dir="$DEFAULT_INSTALL_DIR"
config_dir="$DEFAULT_CONFIG_DIR"
data_dir="$DEFAULT_DATA_DIR"
service_name="$DEFAULT_SERVICE_NAME"
listen_address="0.0.0.0"
port="8080"
stage_dir=""

usage() {
  cat <<'EOF'
Usage: sudo ./deploy/install.sh [options] PUBLISH_DIRECTORY

Install a self-contained McPanel.Api publish directory on Debian or Ubuntu.

Options:
  --listen-address ADDRESS  HTTP bind address (default: 0.0.0.0)
  --port PORT               HTTP port (default: 8080)
  --install-dir PATH        Binary directory (default: /opt/mcpanel)
  --config-dir PATH         Root-only configuration (default: /etc/mcpanel)
  --data-dir PATH           Writable panel data (default: /var/lib/mcpanel)
  --service-name NAME       systemd unit name (default: mcpanel)
  -h, --help                Show this help

The publish directory must contain the self-contained McPanel.Api executable.
The installer does not install Java, Docker, or the .NET runtime.
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

info() {
  printf '%s\n' "$*"
}

cleanup() {
  if [[ -n "$stage_dir" && -d "$stage_dir" ]]; then
    rm -rf -- "$stage_dir"
  fi
}
trap cleanup EXIT

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
    --listen-address)
      (($# >= 2)) || die "--listen-address requires a value"
      listen_address="$2"
      shift 2
      ;;
    --port)
      (($# >= 2)) || die "--port requires a value"
      port="$2"
      shift 2
      ;;
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

[[ "${EUID:-$(id -u)}" -eq 0 ]] || die "run this installer as root (for example, with sudo)"
[[ -n "$artifact_dir" ]] || { usage >&2; die "a publish directory is required"; }

for command_name in chmod chown cp find getent grep groupadd install mktemp mv od realpath sed systemctl tr useradd; do
  command -v "$command_name" >/dev/null 2>&1 || die "required command not found: $command_name"
done

[[ -r /etc/os-release ]] || die "cannot identify this operating system"
# shellcheck disable=SC1091
source /etc/os-release
case "${ID:-}" in
  debian|ubuntu) ;;
  *) die "only Debian and Ubuntu systemd hosts are supported (detected ${ID:-unknown})" ;;
esac

case "$(uname -m)" in
  x86_64|aarch64) ;;
  *) die "only x86_64 and aarch64 hosts are supported" ;;
esac

[[ "$listen_address" =~ ^[][A-Za-z0-9._:-]+$ ]] || die "invalid listen address"
[[ "$port" =~ ^[0-9]+$ ]] || die "port must be an integer"
((port >= 1 && port <= 65535)) || die "port must be between 1 and 65535"
[[ "$service_name" =~ ^[A-Za-z0-9_.@-]+$ ]] || die "invalid service name"

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

artifact_dir="$(realpath -e -- "$artifact_dir")"
[[ -d "$artifact_dir" ]] || die "publish artifact is not a directory: $artifact_dir"
[[ -f "$artifact_dir/McPanel.Api" ]] || die "publish directory does not contain McPanel.Api"
[[ ! -L "$artifact_dir/McPanel.Api" ]] || die "McPanel.Api must not be a symbolic link"
[[ ! -e "$install_dir" ]] || die "$install_dir already exists; use deploy/update.sh for an existing installation"
[[ ! -e "/etc/systemd/system/$service_name.service" ]] || \
  die "/etc/systemd/system/$service_name.service already exists"
[[ ! -e "/etc/systemd/system/$service_name-runtime.service" ]] || \
  die "/etc/systemd/system/$service_name-runtime.service already exists"

for managed_dir in "$config_dir" "$data_dir"; do
  [[ ! -L "$managed_dir" ]] || die "managed directory must not be a symbolic link: $managed_dir"
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
service_template="$script_dir/mcpanel.service.in"
[[ -f "$service_template" && ! -L "$service_template" ]] || die "service template is missing: $service_template"
runtime_service_template="$script_dir/mcpanel-runtime.service.in"
[[ -f "$runtime_service_template" && ! -L "$runtime_service_template" ]] || die "runtime service template is missing: $runtime_service_template"

install_parent="$(dirname -- "$install_dir")"
install -d -o root -g root -m 0755 -- "$install_parent"
stage_dir="$(mktemp -d "$install_parent/.mcpanel-install.XXXXXX")"
cp -a -- "$artifact_dir/." "$stage_dir/"
if find "$stage_dir" -type l -print -quit | grep -q .; then
  die "publish directory contains symbolic links; refusing a privileged installation"
fi
if find "$stage_dir" ! -type d ! -type f -print -quit | grep -q .; then
  die "publish directory contains a device, socket, FIFO, or other special file"
fi
find "$stage_dir" -type d -exec chmod 0755 {} +
find "$stage_dir" -type f -exec chmod 0644 {} +
chmod 0755 "$stage_dir/McPanel.Api"
chown -R root:root "$stage_dir"

if ! getent group "$PANEL_GROUP" >/dev/null; then
  groupadd --system "$PANEL_GROUP"
fi

if getent passwd "$PANEL_USER" >/dev/null; then
  [[ "$(id -gn "$PANEL_USER")" == "$PANEL_GROUP" ]] || \
    die "existing $PANEL_USER user does not use $PANEL_GROUP as its primary group"
  passwd_entry="$(getent passwd "$PANEL_USER")"
  IFS=: read -r _ _ _ _ _ existing_home existing_shell <<< "$passwd_entry"
  [[ "$existing_home" == "$data_dir" ]] || \
    die "existing $PANEL_USER user has home $existing_home instead of $data_dir"
  case "$existing_shell" in
    */nologin|*/false) ;;
    *) die "existing $PANEL_USER user has a login-capable shell: $existing_shell" ;;
  esac
else
  nologin_shell="$(command -v nologin || true)"
  [[ -n "$nologin_shell" ]] || die "nologin shell is not installed"
  useradd --system --gid "$PANEL_GROUP" --home-dir "$data_dir" --shell "$nologin_shell" --no-create-home "$PANEL_USER"
fi

install -d -o root -g "$PANEL_GROUP" -m 0750 -- "$config_dir"
install -d -o "$PANEL_USER" -g "$PANEL_GROUP" -m 0750 -- "$data_dir"
for state_dir in instances staging backups logs runtime runtime/state keys; do
  install -d -o "$PANEL_USER" -g "$PANEL_GROUP" -m 0750 -- "$data_dir/$state_dir"
done

setup_token_file="$config_dir/setup-token"
environment_file="$config_dir/mcpanel.env"
generated_token=""

if [[ -e "$environment_file" ]]; then
  [[ -f "$environment_file" && ! -L "$environment_file" ]] || die "unsafe existing environment file: $environment_file"
  chown root:root "$environment_file"
  chmod 0600 "$environment_file"
  info "Preserving existing $environment_file; listen/port options were not applied."
else
  if [[ -e "$setup_token_file" ]]; then
    [[ -f "$setup_token_file" && ! -L "$setup_token_file" ]] || die "unsafe existing setup token: $setup_token_file"
    generated_token="$(tr -d '\r\n' < "$setup_token_file")"
    [[ "$generated_token" =~ ^[A-Fa-f0-9]{64}$ ]] || die "existing setup token has an unexpected format"
  else
    generated_token="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
    printf '%s\n' "$generated_token" > "$setup_token_file"
  fi

  url_host="$listen_address"
  if [[ "$url_host" == *:* && "$url_host" != \[*\] ]]; then
    url_host="[$url_host]"
  fi

  environment_tmp="$(mktemp "$config_dir/.mcpanel.env.XXXXXX")"
  {
    printf 'ASPNETCORE_ENVIRONMENT=Production\n'
    printf 'ASPNETCORE_URLS=http://%s:%s\n' "$url_host" "$port"
    printf 'MCPANEL_DATA_DIR=%s\n' "$data_dir"
    printf 'MCPANEL_CONFIG_DIR=%s\n' "$config_dir"
    printf 'MCPANEL_SETUP_TOKEN=%s\n' "$generated_token"
  } > "$environment_tmp"
  chown root:root "$environment_tmp"
  chmod 0600 "$environment_tmp"
  mv -- "$environment_tmp" "$environment_file"
fi

if [[ -e "$setup_token_file" ]]; then
  chown root:root "$setup_token_file"
  chmod 0600 "$setup_token_file"
fi

mv -- "$stage_dir" "$install_dir"
stage_dir=""

unit_tmp="$(mktemp "/etc/systemd/system/.${service_name}.service.XXXXXX")"
sed \
  -e "s|@INSTALL_DIR@|$install_dir|g" \
  -e "s|@CONFIG_DIR@|$config_dir|g" \
  -e "s|@DATA_DIR@|$data_dir|g" \
  -e "s|@SERVICE_NAME@|$service_name|g" \
  "$service_template" > "$unit_tmp"
chown root:root "$unit_tmp"
chmod 0644 "$unit_tmp"
mv -- "$unit_tmp" "/etc/systemd/system/$service_name.service"

runtime_service_name="$service_name-runtime"
runtime_unit_tmp="$(mktemp "/etc/systemd/system/.${runtime_service_name}.service.XXXXXX")"
sed \
  -e "s|@INSTALL_DIR@|$install_dir|g" \
  -e "s|@CONFIG_DIR@|$config_dir|g" \
  -e "s|@DATA_DIR@|$data_dir|g" \
  "$runtime_service_template" > "$runtime_unit_tmp"
chown root:root "$runtime_unit_tmp"
chmod 0644 "$runtime_unit_tmp"
mv -- "$runtime_unit_tmp" "/etc/systemd/system/$runtime_service_name.service"

systemctl daemon-reload
systemctl enable --now "$runtime_service_name.service"
systemctl enable --now "$service_name.service"

info "MC Panel was installed and started as $PANEL_USER."
info "Panel URL: http://$listen_address:$port/"
if [[ -n "$generated_token" ]]; then
  info "First-run setup token: $generated_token"
  info "The root-only copy is $setup_token_file. It is ignored after the admin account exists."
else
  info "Existing configuration was retained. The setup token, if still needed, is under $config_dir."
fi
info "Check status with: systemctl status $service_name.service"
info "Runtime status: systemctl status $runtime_service_name.service"

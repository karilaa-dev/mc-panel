#!/usr/bin/env bash

set -Eeuo pipefail
umask 077

readonly DEFAULT_INSTALL_DIR="/opt/mcpanel"
readonly DEFAULT_CONFIG_DIR="/etc/mcpanel"
readonly DEFAULT_DATA_DIR="/var/lib/mcpanel"
readonly DEFAULT_SERVICE_NAME="mcpanel"
readonly DEFAULT_PORT="6050"
readonly DEFAULT_INSTALL_SOURCE="github"
readonly DEFAULT_RELEASE="main"
readonly GITHUB_REPOSITORY="karilaa-dev/mc-panel"
readonly RELEASE_MANIFEST_NAME="release-manifest.txt"
readonly RELEASE_METADATA_NAME=".mcpanel-release"
readonly GITHUB_RELEASE_BASE_URL="${MCPANEL_RELEASE_BASE_URL:-https://github.com/$GITHUB_REPOSITORY/releases/download}"
readonly PANEL_USER="mcpanel"
readonly PANEL_GROUP="mcpanel"

script_path="$(realpath -e -- "${BASH_SOURCE[0]}")"
repo_root="$(dirname -- "$script_path")"

usage() {
  cat <<'EOF'
Usage: ./mcpanel.sh COMMAND [options]

Build and manage MC Panel as a Debian/Ubuntu systemd installation.

Commands:
  install             Download and install MC Panel
  update              Download and update MC Panel
  import-server SOURCE  Import an existing Minecraft server
  uninstall           Remove services and binaries, preserving all data
  purge               Permanently remove services, binaries, and all data
  build OUTPUT         Build a self-contained artifact without installing it
  status              Show service and HTTP status
  help                 Show this help

Install options:
  --source github|local    Artifact source (default: github)
  --release REF            GitHub release tag (default: main)
  --listen-address ADDRESS  HTTP bind address (default: 0.0.0.0)
  --port PORT               HTTP port (default: 6050)
  --install-dir PATH        Binary directory (default: /opt/mcpanel)
  --config-dir PATH         Configuration directory (default: /etc/mcpanel)
  --data-dir PATH           Data directory (default: /var/lib/mcpanel)
  --service-name NAME       systemd unit name (default: mcpanel)

Update options:
  --source github|local    Artifact source (default: github)
  --release REF            GitHub release tag (default: main)
  --install-dir PATH        Binary directory (default: /opt/mcpanel)
  --config-dir PATH         Configuration directory (default: /etc/mcpanel)
  --data-dir PATH           Data directory (default: /var/lib/mcpanel)
  --service-name NAME       systemd unit name (default: mcpanel)

Import-server options:
  --name NAME               Managed server name
  --kind KIND               vanilla|paper|fabric|forge|neoforge
  --version VERSION         Minecraft version
  --loader-version VERSION  Required for Fabric, Forge, and NeoForge
  --launch-target PATH      Relative server JAR or unix_args.txt
  --java-runtime ID|PATH    Registered runtime ID or absolute Java path
  --memory-mb MIB           Java heap in MiB
  --port PORT               Game port (defaults to server.properties)
  --jvm-args ARGUMENTS      Extra JVM arguments
  --accept-eula             Accept the Minecraft EULA
  --non-interactive         Fail instead of prompting for missing values
  --dry-run                 Validate without changing panel state
  --json                    Emit one JSON result and imply --non-interactive
  --install-dir PATH        Binary directory (default: /opt/mcpanel)
  --config-dir PATH         Configuration directory (default: /etc/mcpanel)
  --data-dir PATH           Data directory (default: /var/lib/mcpanel)
  --service-name NAME       systemd unit name (default: mcpanel)

Uninstall/purge options:
  --install-dir PATH        Binary directory (default: /opt/mcpanel)
  --config-dir PATH         Configuration directory (default: /etc/mcpanel)
  --data-dir PATH           Data directory (default: /var/lib/mcpanel)
  --service-name NAME       systemd unit name (default: mcpanel)

Purge additionally requires --yes-really-purge.
Build accepts --rid linux-x64|linux-arm64; otherwise host architecture is used.
Status accepts --config-dir and --service-name.

Import-server accepts an unpacked directory, .zip, .tar, .tar.gz, or .tgz.
The archive contents must be the server root, not a containing directory.
The source is preserved. A real import briefly pauses the web panel while
existing managed servers remain online in the persistent runtime.

Run install, update, and import-server as a regular user. GitHub artifacts are used by default.
Use --source local to build and install the current checkout. The script uses
passwordless sudo only for system changes.
EOF
}

die() {
  printf 'error: %s\n' "$*" >&2
  exit 1
}

info() {
  printf '%s\n' "$*"
}

warn() {
  printf 'warning: %s\n' "$*" >&2
}

die_import() {
  local json="$1" exit_code="$2" code="$3" message="$4"
  if ((json)); then
    message="${message//\\/\\\\}"
    message="${message//\"/\\\"}"
    message="${message//$'\n'/\\n}"
    printf '{"ok":false,"code":"%s","message":"%s"}\n' "$code" "$message"
  else
    printf 'error: %s\n' "$message" >&2
  fi
  exit "$exit_code"
}

require_commands() {
  local command_name
  for command_name in "$@"; do
    command -v "$command_name" >/dev/null 2>&1 || die "required command not found: $command_name"
  done
}

require_regular_user() {
  [[ "${EUID:-$(id -u)}" -ne 0 ]] || die "run this command as a regular user; the script invokes sudo for system changes"
}

require_root() {
  [[ "${EUID:-$(id -u)}" -eq 0 ]] || die "internal system operation requires root"
}

require_passwordless_sudo() {
  require_commands sudo
  sudo -n true 2>/dev/null || die "passwordless sudo is required for system installation commands"
}

detect_rid() {
  case "$(uname -m)" in
    x86_64) printf 'linux-x64\n' ;;
    aarch64) printf 'linux-arm64\n' ;;
    *) die "unsupported architecture: $(uname -m)" ;;
  esac
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

validate_managed_paths() {
  local first_path second_path path_pair
  for path_pair in "$1:$2" "$1:$3" "$2:$3"; do
    first_path="${path_pair%%:*}"
    second_path="${path_pair#*:}"
    if [[ "$first_path" == "$second_path" || "$first_path" == "$second_path/"* || "$second_path" == "$first_path/"* ]]; then
      die "install, configuration, and data directories must not overlap"
    fi
  done
}

validate_service_name() {
  [[ "$1" =~ ^[A-Za-z0-9_.@-]+$ ]] || die "invalid service name"
}

validate_host() {
  [[ -r /etc/os-release ]] || die "cannot identify this operating system"
  # shellcheck disable=SC1091
  source /etc/os-release
  case "${ID:-}" in
    debian|ubuntu) ;;
    *) die "only Debian and Ubuntu systemd hosts are supported (detected ${ID:-unknown})" ;;
  esac
  detect_rid >/dev/null
}

validate_artifact() {
  local artifact_dir="$1"
  [[ -d "$artifact_dir" ]] || die "publish artifact is not a directory: $artifact_dir"
  [[ -f "$artifact_dir/McPanel.Api" && ! -L "$artifact_dir/McPanel.Api" ]] || \
    die "publish directory does not contain a regular McPanel.Api executable"
  [[ -f "$artifact_dir/wwwroot/index.html" && ! -L "$artifact_dir/wwwroot/index.html" ]] || \
    die "publish directory does not contain the web client"
}

validate_install_source() {
  case "$1" in
    github|local) ;;
    *) die "install source must be github or local" ;;
  esac
}

validate_release_ref() {
  [[ "$1" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$ ]] || die "invalid release tag: $1"
}

validate_rid() {
  case "$1" in
    linux-x64|linux-arm64) ;;
    *) die "unsupported runtime identifier: $1" ;;
  esac
}

manifest_commit=""
manifest_script_sha256=""
manifest_linux_x64_sha256=""
manifest_linux_arm64_sha256=""

parse_release_manifest() {
  local manifest_file="$1"
  local -a lines=()

  [[ -f "$manifest_file" && ! -L "$manifest_file" ]] || die "release manifest is missing or unsafe"
  mapfile -t lines < "$manifest_file"
  [[ ${#lines[@]} -eq 5 ]] || die "release manifest must contain exactly five lines"
  [[ "${lines[0]}" == "schema=1" ]] || die "unsupported release manifest schema"
  [[ "${lines[1]}" == commit=* ]] || die "release manifest is missing commit"
  [[ "${lines[2]}" == script_sha256=* ]] || die "release manifest is missing script checksum"
  [[ "${lines[3]}" == linux_x64_sha256=* ]] || die "release manifest is missing linux-x64 checksum"
  [[ "${lines[4]}" == linux_arm64_sha256=* ]] || die "release manifest is missing linux-arm64 checksum"

  manifest_commit="${lines[1]#commit=}"
  manifest_script_sha256="${lines[2]#script_sha256=}"
  manifest_linux_x64_sha256="${lines[3]#linux_x64_sha256=}"
  manifest_linux_arm64_sha256="${lines[4]#linux_arm64_sha256=}"
  [[ "$manifest_commit" =~ ^[a-f0-9]{40}$ ]] || die "release manifest commit is invalid"
  [[ "$manifest_script_sha256" =~ ^[a-f0-9]{64}$ ]] || die "release manifest script checksum is invalid"
  [[ "$manifest_linux_x64_sha256" =~ ^[a-f0-9]{64}$ ]] || die "release manifest linux-x64 checksum is invalid"
  [[ "$manifest_linux_arm64_sha256" =~ ^[a-f0-9]{64}$ ]] || die "release manifest linux-arm64 checksum is invalid"
}

metadata_release=""
metadata_commit=""
metadata_rid=""

parse_release_metadata() {
  local metadata_file="$1"
  local -a lines=()

  [[ -f "$metadata_file" && ! -L "$metadata_file" ]] || die "release metadata is missing or unsafe"
  mapfile -t lines < "$metadata_file"
  [[ ${#lines[@]} -eq 4 ]] || die "release metadata must contain exactly four lines"
  [[ "${lines[0]}" == "schema=1" ]] || die "unsupported release metadata schema"
  [[ "${lines[1]}" == release=* ]] || die "release metadata is missing release"
  [[ "${lines[2]}" == commit=* ]] || die "release metadata is missing commit"
  [[ "${lines[3]}" == rid=* ]] || die "release metadata is missing runtime identifier"

  metadata_release="${lines[1]#release=}"
  metadata_commit="${lines[2]#commit=}"
  metadata_rid="${lines[3]#rid=}"
  validate_release_ref "$metadata_release"
  [[ "$metadata_commit" =~ ^[a-f0-9]{40}$ ]] || die "release metadata commit is invalid"
  validate_rid "$metadata_rid"
}

validate_release_metadata() {
  local artifact_dir="$1" expected_release="$2" expected_commit="$3" expected_rid="$4"
  parse_release_metadata "$artifact_dir/$RELEASE_METADATA_NAME"
  [[ "$metadata_release" == "$expected_release" ]] || die "artifact release metadata does not match $expected_release"
  [[ "$metadata_commit" == "$expected_commit" ]] || die "artifact commit metadata does not match the release manifest"
  [[ "$metadata_rid" == "$expected_rid" ]] || die "artifact runtime metadata does not match $expected_rid"
}

installed_release_matches() {
  local artifact_dir="$1" install_dir="$2"
  local incoming_identity installed_identity
  [[ -f "$artifact_dir/$RELEASE_METADATA_NAME" && -f "$install_dir/$RELEASE_METADATA_NAME" ]] || return 1
  incoming_identity="$(parse_release_metadata "$artifact_dir/$RELEASE_METADATA_NAME"; printf '%s/%s\n' "$metadata_commit" "$metadata_rid")" || return 1
  installed_identity="$(parse_release_metadata "$install_dir/$RELEASE_METADATA_NAME"; printf '%s/%s\n' "$metadata_commit" "$metadata_rid")" || return 1
  [[ "$incoming_identity" == "$installed_identity" ]]
}

verify_sha256() {
  local expected="$1" file_path="$2" actual
  actual="$(sha256sum --binary -- "$file_path")"
  actual="${actual%% *}"
  [[ "$actual" == "$expected" ]] || die "checksum mismatch for $(basename -- "$file_path")"
}

validate_archive_members() {
  local archive="$1" listing="$2" member normalized
  tar --list --gzip --file "$archive" > "$listing" || die "release archive could not be read"
  while IFS= read -r member; do
    normalized="${member#./}"
    [[ -n "$normalized" ]] || continue
    [[ "$normalized" != /* ]] || die "release archive contains an absolute path"
    case "/$normalized/" in
      */../*) die "release archive contains a parent-directory path" ;;
    esac
  done < "$listing"
}

validate_extracted_tree() {
  local artifact_dir="$1"
  if find "$artifact_dir" -type l -print -quit | grep -q .; then die "release archive contains symbolic links"; fi
  if find "$artifact_dir" ! -type d ! -type f -print -quit | grep -q .; then die "release archive contains a special file"; fi
}

download_release_asset() {
  local release="$1" asset_name="$2" destination="$3"
  curl --location --fail --silent --show-error \
    --retry 2 --retry-delay 1 --connect-timeout 15 --max-time 300 \
    --output "$destination" \
    "$GITHUB_RELEASE_BASE_URL/$release/$asset_name"
}

prepare_remote_release_attempt() {
  local release="$1" rid="$2" attempt_dir="$3"
  local manifest_file installer_file archive_file archive_name installer_name expected_archive_sha
  local artifact_dir="$attempt_dir/artifact"

  mkdir -p -- "$attempt_dir" "$artifact_dir"
  manifest_file="$attempt_dir/$RELEASE_MANIFEST_NAME"
  download_release_asset "$release" "$RELEASE_MANIFEST_NAME" "$manifest_file"
  parse_release_manifest "$manifest_file"

  installer_name="mcpanel-$manifest_commit.sh"
  archive_name="mcpanel-$manifest_commit-$rid.tar.gz"
  installer_file="$attempt_dir/$installer_name"
  archive_file="$attempt_dir/$archive_name"
  case "$rid" in
    linux-x64) expected_archive_sha="$manifest_linux_x64_sha256" ;;
    linux-arm64) expected_archive_sha="$manifest_linux_arm64_sha256" ;;
    *) die "unsupported runtime identifier: $rid" ;;
  esac

  download_release_asset "$release" "$installer_name" "$installer_file"
  download_release_asset "$release" "$archive_name" "$archive_file"
  verify_sha256 "$manifest_script_sha256" "$installer_file"
  verify_sha256 "$expected_archive_sha" "$archive_file"
  validate_archive_members "$archive_file" "$attempt_dir/archive-members.txt"
  tar --extract --gzip --file "$archive_file" --directory "$artifact_dir" \
    --no-same-owner --no-same-permissions
  validate_extracted_tree "$artifact_dir"
  validate_artifact "$artifact_dir"
  validate_release_metadata "$artifact_dir" "$release" "$manifest_commit" "$rid"
  chmod 0755 "$installer_file"
  printf '%s\n' "$manifest_commit" > "$attempt_dir/commit"
}

prepare_remote_release() {
  local release="$1" rid="$2" work_root="$3" attempt attempt_dir
  for attempt in 1 2 3; do
    attempt_dir="$work_root/attempt-$attempt"
    if (prepare_remote_release_attempt "$release" "$rid" "$attempt_dir"); then
      printf '%s\n' "$attempt_dir"
      return 0
    fi
    warn "release download attempt $attempt of 3 failed"
  done
  return 1
}

publish_artifact() {
  local rid="$1"
  local output_arg="$2"
  local output_dir output_parent stage_dir=""
  local web_project="$repo_root/src/McPanel.Web"
  local api_project="$repo_root/src/McPanel.Api/McPanel.Api.csproj"

  require_regular_user
  require_commands chmod dotnet mktemp mkdir mv npm realpath uname
  case "$rid" in
    linux-x64|linux-arm64) ;;
    *) die "unsupported runtime identifier: $rid" ;;
  esac
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
  cleanup_publish() {
    local rc=$?
    if [[ -n "$stage_dir" && -d "$stage_dir" ]]; then rm -rf -- "$stage_dir"; fi
    trap - RETURN
    return "$rc"
  }
  trap cleanup_publish RETURN

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
  [[ -f "$stage_dir/wwwroot/index.html" ]] || die "publish completed without the web client"
  chmod 0755 "$stage_dir/McPanel.Api"
  mv -- "$stage_dir" "$output_dir"
  stage_dir=""
  trap - RETURN
  info "Self-contained $rid artifact: $output_dir"
}

render_panel_unit() {
  local install_dir="$1" config_dir="$2" data_dir="$3" service_name="$4"
  cat <<EOF
[Unit]
Description=MC Panel Minecraft server manager
Wants=network-online.target
Wants=$service_name-runtime.service
After=network-online.target $service_name-runtime.service
ConditionFileIsExecutable=$install_dir/McPanel.Api

[Service]
Type=simple
User=$PANEL_USER
Group=$PANEL_GROUP
WorkingDirectory=$data_dir
EnvironmentFile=$config_dir/mcpanel.env
ExecStart=$install_dir/McPanel.Api

Restart=on-failure
RestartSec=5s
TimeoutStartSec=30s
TimeoutStopSec=75s
KillSignal=SIGTERM
KillMode=mixed
SendSIGKILL=yes

UMask=0077
NoNewPrivileges=yes
CapabilityBoundingSet=
AmbientCapabilities=
PrivateTmp=yes
PrivateDevices=yes
ProtectSystem=strict
ProtectHome=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=yes
ProtectClock=yes
ProtectHostname=yes
ProtectProc=invisible
RestrictRealtime=yes
RestrictSUIDSGID=yes
RestrictNamespaces=yes
LockPersonality=yes
RemoveIPC=yes
KeyringMode=private
SystemCallArchitectures=native
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
ReadOnlyPaths=$install_dir $config_dir
ReadWritePaths=$data_dir
LimitNOFILE=65536
TasksMax=8192

[Install]
WantedBy=multi-user.target
EOF
}

render_runtime_unit() {
  local install_dir="$1" config_dir="$2" data_dir="$3"
  cat <<EOF
[Unit]
Description=MC Panel persistent Minecraft runtime
Wants=network-online.target
After=network-online.target
ConditionFileIsExecutable=$install_dir/McPanel.Api

[Service]
Type=simple
User=$PANEL_USER
Group=$PANEL_GROUP
WorkingDirectory=$data_dir
EnvironmentFile=$config_dir/mcpanel.env
ExecStart=$install_dir/McPanel.Api --mcpanel-runtime-host

Restart=always
RestartSec=5s
TimeoutStartSec=30s
TimeoutStopSec=75s
KillSignal=SIGTERM
KillMode=mixed
SendSIGKILL=yes
Delegate=memory
MemoryAccounting=yes

UMask=0077
NoNewPrivileges=yes
CapabilityBoundingSet=
AmbientCapabilities=
PrivateTmp=yes
PrivateDevices=yes
ProtectSystem=strict
ProtectHome=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectKernelLogs=yes
ProtectControlGroups=no
ProtectClock=yes
ProtectHostname=yes
ProtectProc=invisible
RestrictRealtime=yes
RestrictSUIDSGID=yes
RestrictNamespaces=yes
LockPersonality=yes
RemoveIPC=yes
KeyringMode=private
SystemCallArchitectures=native
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
ReadOnlyPaths=$install_dir $config_dir
ReadWritePaths=$data_dir
LimitNOFILE=65536
TasksMax=8192

[Install]
WantedBy=multi-user.target
EOF
}

wait_for_active() {
  local service="$1"
  local consecutive=0
  local attempt
  for attempt in {1..30}; do
    if systemctl is-active --quiet "$service"; then
      ((consecutive += 1))
      if ((consecutive >= 3)); then return 0; fi
    else
      consecutive=0
    fi
    sleep 1
  done
  return 1
}

runtime_socket_ready() {
  [[ -S "$1" ]]
}

runtime_service_pid() {
  systemctl show --property MainPID --value "$1" 2>/dev/null || true
}

wait_for_runtime_ready() {
  local service="$1" socket="$2"
  local consecutive=0
  local attempt
  for attempt in {1..45}; do
    if systemctl is-active --quiet "$service" && runtime_socket_ready "$socket"; then
      ((consecutive += 1))
      if ((consecutive >= 3)); then return 0; fi
    else
      consecutive=0
    fi
    sleep 1
  done
  return 1
}

wait_for_runtime_generation() {
  local service="$1" socket="$2" old_pid="$3"
  local consecutive=0 current_pid
  local attempt
  for attempt in {1..45}; do
    current_pid="$(runtime_service_pid "$service")"
    if [[ "$current_pid" =~ ^[1-9][0-9]*$ && "$current_pid" != "$old_pid" ]] &&
       systemctl is-active --quiet "$service" && runtime_socket_ready "$socket"; then
      ((consecutive += 1))
      if ((consecutive >= 3)); then return 0; fi
    else
      consecutive=0
    fi
    sleep 1
  done
  return 1
}

http_probe_url() {
  local config_dir="$1"
  local url
  url="$(awk -F= '$1 == "ASPNETCORE_URLS" { print substr($0, index($0, "=") + 1); exit }' "$config_dir/mcpanel.env" 2>/dev/null || true)"
  [[ -n "$url" ]] || return 1
  case "$url" in
    http://0.0.0.0:*) url="http://127.0.0.1:${url##*:}" ;;
    'http://[::]:'*) url="http://[::1]:${url##*:}" ;;
  esac
  printf '%s/api/v1/auth/status\n' "${url%/}"
}

wait_for_http() {
  local config_dir="$1"
  local probe_url attempt
  probe_url="$(http_probe_url "$config_dir")" || return 1
  for attempt in {1..30}; do
    if curl --noproxy '*' --fail --silent --max-time 5 "$probe_url" >/dev/null; then return 0; fi
    sleep 1
  done
  return 1
}

root_install() {
  require_root
  local artifact_dir="$1" install_dir="$2" config_dir="$3" data_dir="$4"
  local service_name="$5" listen_address="$6" port="$7"
  local stage_dir="" install_started=0 install_activated=0 panel_unit_created=0 runtime_unit_created=0 install_succeeded=0
  local setup_token_file environment_file generated_token="" environment_tmp url_host
  local install_parent service_unit runtime_service_name runtime_unit unit_tmp="" runtime_unit_tmp=""

  install_cleanup() {
    local rc=$?
    set +e
    if ((rc != 0 && !install_succeeded)); then
      if ((panel_unit_created)); then systemctl disable --now "$service_name.service" >/dev/null 2>&1 || true; fi
      if ((runtime_unit_created)); then systemctl disable --now "$service_name-runtime.service" >/dev/null 2>&1 || true; fi
      if ((panel_unit_created)); then rm -f -- "$service_unit"; fi
      if ((runtime_unit_created)); then rm -f -- "$runtime_unit"; fi
      if ((install_activated)) && [[ -d "$install_dir" && ! -L "$install_dir" ]]; then rm -rf --one-file-system -- "$install_dir"; fi
      systemctl daemon-reload >/dev/null 2>&1 || true
      if ((install_started)); then
        warn "installation failed; services and binaries created by this attempt were removed"
        warn "configuration and data were preserved under $config_dir and $data_dir"
      fi
    fi
    if [[ -n "$stage_dir" && -d "$stage_dir" ]]; then rm -rf -- "$stage_dir"; fi
    if [[ -n "$unit_tmp" && -f "$unit_tmp" ]]; then rm -f -- "$unit_tmp"; fi
    if [[ -n "$runtime_unit_tmp" && -f "$runtime_unit_tmp" ]]; then rm -f -- "$runtime_unit_tmp"; fi
    trap - EXIT
    exit "$rc"
  }
  trap install_cleanup EXIT

  require_commands awk chmod chown cp curl find getent grep groupadd install mktemp mv od realpath sed sleep systemctl tr useradd
  validate_host
  validate_service_name "$service_name"
  [[ "$listen_address" =~ ^[][A-Za-z0-9._:-]+$ ]] || die "invalid listen address"
  [[ "$port" =~ ^[0-9]+$ ]] || die "port must be an integer"
  ((port >= 1 && port <= 65535)) || die "port must be between 1 and 65535"
  install_dir="$(normalize_managed_path "$install_dir")"
  config_dir="$(normalize_managed_path "$config_dir")"
  data_dir="$(normalize_managed_path "$data_dir")"
  validate_managed_paths "$install_dir" "$config_dir" "$data_dir"
  artifact_dir="$(realpath -e -- "$artifact_dir")"
  validate_artifact "$artifact_dir"

  service_unit="/etc/systemd/system/$service_name.service"
  runtime_service_name="$service_name-runtime"
  runtime_unit="/etc/systemd/system/$runtime_service_name.service"
  [[ ! -e "$install_dir" ]] || die "$install_dir already exists; use ./mcpanel.sh update"
  [[ ! -e "$service_unit" ]] || die "$service_unit already exists"
  [[ ! -e "$runtime_unit" ]] || die "$runtime_unit already exists"
  for managed_dir in "$config_dir" "$data_dir"; do
    [[ ! -L "$managed_dir" ]] || die "managed directory must not be a symbolic link: $managed_dir"
  done
  install_started=1

  install_parent="$(dirname -- "$install_dir")"
  install -d -o root -g root -m 0755 -- "$install_parent"
  stage_dir="$(mktemp -d "$install_parent/.mcpanel-install.XXXXXX")"
  cp -a -- "$artifact_dir/." "$stage_dir/"
  if find "$stage_dir" -type l -print -quit | grep -q .; then die "publish directory contains symbolic links"; fi
  if find "$stage_dir" ! -type d ! -type f -print -quit | grep -q .; then die "publish directory contains a special file"; fi
  find "$stage_dir" -type d -exec chmod 0755 {} +
  find "$stage_dir" -type f -exec chmod 0644 {} +
  chmod 0755 "$stage_dir/McPanel.Api"
  chown -R root:root "$stage_dir"

  if ! getent group "$PANEL_GROUP" >/dev/null; then groupadd --system "$PANEL_GROUP"; fi
  if getent passwd "$PANEL_USER" >/dev/null; then
    [[ "$(id -gn "$PANEL_USER")" == "$PANEL_GROUP" ]] || die "existing $PANEL_USER user has an unexpected primary group"
    local passwd_entry existing_home existing_shell
    passwd_entry="$(getent passwd "$PANEL_USER")"
    IFS=: read -r _ _ _ _ _ existing_home existing_shell <<< "$passwd_entry"
    [[ "$existing_home" == "$data_dir" ]] || die "existing $PANEL_USER user has home $existing_home instead of $data_dir"
    case "$existing_shell" in */nologin|*/false) ;; *) die "existing $PANEL_USER user has a login-capable shell" ;; esac
  else
    local nologin_shell
    nologin_shell="$(command -v nologin || true)"
    [[ -n "$nologin_shell" ]] || die "nologin shell is not installed"
    useradd --system --gid "$PANEL_GROUP" --home-dir "$data_dir" --shell "$nologin_shell" --no-create-home "$PANEL_USER"
  fi

  install -d -o root -g "$PANEL_GROUP" -m 0750 -- "$config_dir"
  install -d -o "$PANEL_USER" -g "$PANEL_GROUP" -m 0750 -- "$data_dir"
  local state_dir
  for state_dir in instances staging backups logs runtime runtime/state keys; do
    install -d -o "$PANEL_USER" -g "$PANEL_GROUP" -m 0750 -- "$data_dir/$state_dir"
  done

  setup_token_file="$config_dir/setup-token"
  environment_file="$config_dir/mcpanel.env"
  if [[ -e "$environment_file" ]]; then
    [[ -f "$environment_file" && ! -L "$environment_file" ]] || die "unsafe existing environment file: $environment_file"
    chown root:root "$environment_file"
    chmod 0600 "$environment_file"
    info "Preserving existing $environment_file; listen and port options were not applied."
  else
    if [[ -e "$setup_token_file" ]]; then
      [[ -f "$setup_token_file" && ! -L "$setup_token_file" ]] || die "unsafe existing setup token"
      generated_token="$(tr -d '\r\n' < "$setup_token_file")"
      [[ "$generated_token" =~ ^[A-Fa-f0-9]{64}$ ]] || die "existing setup token has an unexpected format"
    else
      generated_token="$(od -An -N32 -tx1 /dev/urandom | tr -d ' \n')"
      printf '%s\n' "$generated_token" > "$setup_token_file"
    fi
    url_host="$listen_address"
    if [[ "$url_host" == *:* && "$url_host" != \[*\] ]]; then url_host="[$url_host]"; fi
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
  if [[ -e "$setup_token_file" ]]; then chown root:root "$setup_token_file"; chmod 0600 "$setup_token_file"; fi

  unit_tmp="$(mktemp "/etc/systemd/system/.${service_name}.service.XXXXXX")"
  render_panel_unit "$install_dir" "$config_dir" "$data_dir" "$service_name" > "$unit_tmp"
  chown root:root "$unit_tmp"; chmod 0644 "$unit_tmp"
  runtime_unit_tmp="$(mktemp "/etc/systemd/system/.${runtime_service_name}.service.XXXXXX")"
  render_runtime_unit "$install_dir" "$config_dir" "$data_dir" > "$runtime_unit_tmp"
  chown root:root "$runtime_unit_tmp"; chmod 0644 "$runtime_unit_tmp"

  mv -- "$stage_dir" "$install_dir"; stage_dir=""; install_activated=1
  mv -- "$unit_tmp" "$service_unit"; panel_unit_created=1
  mv -- "$runtime_unit_tmp" "$runtime_unit"; runtime_unit_created=1
  systemctl daemon-reload
  systemctl enable --now "$runtime_service_name.service"
  systemctl enable --now "$service_name.service"
  wait_for_active "$runtime_service_name.service" || die "$runtime_service_name.service did not remain active"
  wait_for_active "$service_name.service" || die "$service_name.service did not remain active"
  wait_for_http "$config_dir" || die "the panel HTTP endpoint did not become ready"

  install_succeeded=1
  trap - EXIT
  info "MC Panel was installed and started as $PANEL_USER."
  info "Panel URL: http://$listen_address:$port/"
  if [[ -n "$generated_token" ]]; then
    info "First-run setup token: $generated_token"
    info "The root-only copy is $setup_token_file."
  else
    info "Existing configuration and setup state were retained."
  fi
}

root_update() {
  require_root
  local artifact_dir="$1" install_dir="$2" config_dir="$3" data_dir="$4" service_name="$5"
  local stage_dir="" rollback_dir="" update_swapped=0 update_succeeded=0 was_active=0 was_runtime_active=0 panel_stopped=0
  local old_unit_backup="" old_runtime_unit_backup="" old_memory_dropin_backup=""
  local service_unit runtime_service_name runtime_unit memory_dropin_dir memory_dropin install_parent
  local unit_tmp="" runtime_unit_tmp="" failed_dir="" runtime_socket old_runtime_pid="0" current_runtime_pid="0" runtime_upgrade_result=""

  update_cleanup() {
    local rc=$?
    set +e
    if ((rc != 0 && update_swapped && !update_succeeded)) && [[ -n "$rollback_dir" && -d "$rollback_dir" ]]; then
      failed_dir="${install_dir}.failed-$(date -u +%Y%m%dT%H%M%SZ)-$$"
      warn "update failed; restoring $rollback_dir"
      systemctl stop "$service_name.service" >/dev/null 2>&1 || true
      if [[ -d "$install_dir" && ! -L "$install_dir" ]]; then mv -- "$install_dir" "$failed_dir"; fi
      mv -- "$rollback_dir" "$install_dir"
      rollback_dir=""
      cp -- "$old_unit_backup" "$service_unit"
      if [[ -n "$old_runtime_unit_backup" ]]; then
        cp -- "$old_runtime_unit_backup" "$runtime_unit"
      else
        systemctl disable --now "$runtime_service_name.service" >/dev/null 2>&1 || true
        rm -f -- "$runtime_unit"
      fi
      if [[ -n "$old_memory_dropin_backup" ]]; then
        mkdir -p -- "$memory_dropin_dir"
        cp -- "$old_memory_dropin_backup" "$memory_dropin"
      else
        rm -f -- "$memory_dropin"
      fi
      systemctl daemon-reload >/dev/null 2>&1 || true
      if ((was_runtime_active)); then systemctl enable --now "$runtime_service_name.service" >/dev/null 2>&1 || warn "the old runtime service could not be restarted"; fi
      if ((was_active)); then systemctl start "$service_name.service" >/dev/null 2>&1 || warn "the old panel service could not be restarted"; fi
      warn "previous binaries were restored; failed files were retained at $failed_dir"
    elif ((rc != 0 && panel_stopped && was_active)); then
      systemctl start "$service_name.service" >/dev/null 2>&1 || warn "the unchanged panel service could not be restarted"
    fi
    if [[ -n "$stage_dir" && -d "$stage_dir" ]]; then rm -rf -- "$stage_dir"; fi
    if [[ -n "$unit_tmp" && -f "$unit_tmp" ]]; then rm -f -- "$unit_tmp"; fi
    if [[ -n "$runtime_unit_tmp" && -f "$runtime_unit_tmp" ]]; then rm -f -- "$runtime_unit_tmp"; fi
    local backup
    for backup in "$old_unit_backup" "$old_runtime_unit_backup" "$old_memory_dropin_backup"; do
      if [[ -n "$backup" && -f "$backup" ]]; then rm -f -- "$backup"; fi
    done
    trap - EXIT
    exit "$rc"
  }
  trap update_cleanup EXIT

  require_commands awk chmod chown cp curl date env find grep mkdir mktemp mv realpath rm rmdir runuser sleep systemctl
  validate_host
  validate_service_name "$service_name"
  install_dir="$(normalize_managed_path "$install_dir")"
  config_dir="$(normalize_managed_path "$config_dir")"
  data_dir="$(normalize_managed_path "$data_dir")"
  validate_managed_paths "$install_dir" "$config_dir" "$data_dir"
  service_unit="/etc/systemd/system/$service_name.service"
  runtime_service_name="$service_name-runtime"
  runtime_unit="/etc/systemd/system/$runtime_service_name.service"
  runtime_socket="$data_dir/runtime/control.sock"
  memory_dropin_dir="/etc/systemd/system/$service_name.service.d"
  memory_dropin="$memory_dropin_dir/50-mcpanel-memory.conf"
  [[ -d "$install_dir" && ! -L "$install_dir" ]] || die "installation is missing or unsafe: $install_dir"
  [[ -f "$install_dir/McPanel.Api" && ! -L "$install_dir/McPanel.Api" ]] || die "current executable is missing"
  [[ -f "$service_unit" && ! -L "$service_unit" ]] || die "systemd unit is missing or unsafe: $service_unit"
  [[ ! -e "$runtime_unit" || -f "$runtime_unit" && ! -L "$runtime_unit" ]] || die "runtime unit is unsafe"
  [[ ! -e "$memory_dropin" || -f "$memory_dropin" && ! -L "$memory_dropin" ]] || die "memory drop-in is unsafe"
  artifact_dir="$(realpath -e -- "$artifact_dir")"
  validate_artifact "$artifact_dir"
  [[ "$artifact_dir" != "$install_dir" && "$artifact_dir" != "$install_dir/"* ]] || die "artifact must be outside the active installation"

  if installed_release_matches "$artifact_dir" "$install_dir"; then
    parse_release_metadata "$artifact_dir/$RELEASE_METADATA_NAME"
    update_succeeded=1
    trap - EXIT
    info "MC Panel is already at $metadata_release commit $metadata_commit for $metadata_rid."
    return 0
  fi

  install_parent="$(dirname -- "$install_dir")"
  stage_dir="$(mktemp -d "$install_parent/.mcpanel-update.XXXXXX")"
  cp -a -- "$artifact_dir/." "$stage_dir/"
  if find "$stage_dir" -type l -print -quit | grep -q .; then die "publish directory contains symbolic links"; fi
  if find "$stage_dir" ! -type d ! -type f -print -quit | grep -q .; then die "publish directory contains a special file"; fi
  find "$stage_dir" -type d -exec chmod 0755 {} +
  find "$stage_dir" -type f -exec chmod 0644 {} +
  chmod 0755 "$stage_dir/McPanel.Api"
  chown -R root:root "$stage_dir"

  old_unit_backup="$(mktemp)"; cp -- "$service_unit" "$old_unit_backup"
  if [[ -e "$runtime_unit" ]]; then old_runtime_unit_backup="$(mktemp)"; cp -- "$runtime_unit" "$old_runtime_unit_backup"; fi
  if [[ -e "$memory_dropin" ]]; then old_memory_dropin_backup="$(mktemp)"; cp -- "$memory_dropin" "$old_memory_dropin_backup"; fi
  if systemctl is-active --quiet "$service_name.service"; then was_active=1; fi
  if systemctl is-active --quiet "$runtime_service_name.service"; then
    was_runtime_active=1
    old_runtime_pid="$(runtime_service_pid "$runtime_service_name.service")"
    [[ "$old_runtime_pid" =~ ^[1-9][0-9]*$ ]] || die "could not determine the active runtime process"
  fi

  rollback_dir="${install_dir}.rollback-$(date -u +%Y%m%dT%H%M%SZ)-$$"
  [[ ! -e "$rollback_dir" ]] || die "rollback destination already exists: $rollback_dir"
  systemctl stop "$service_name.service"
  panel_stopped=1
  mv -- "$install_dir" "$rollback_dir"
  update_swapped=1
  mv -- "$stage_dir" "$install_dir"; stage_dir=""

  rm -f -- "$memory_dropin"
  rmdir --ignore-fail-on-non-empty -- "$memory_dropin_dir" 2>/dev/null || true
  unit_tmp="$(mktemp "/etc/systemd/system/.${service_name}.service.XXXXXX")"
  render_panel_unit "$install_dir" "$config_dir" "$data_dir" "$service_name" > "$unit_tmp"
  chown root:root "$unit_tmp"; chmod 0644 "$unit_tmp"; mv -- "$unit_tmp" "$service_unit"
  runtime_unit_tmp="$(mktemp "/etc/systemd/system/.${runtime_service_name}.service.XXXXXX")"
  render_runtime_unit "$install_dir" "$config_dir" "$data_dir" > "$runtime_unit_tmp"
  chown root:root "$runtime_unit_tmp"; chmod 0644 "$runtime_unit_tmp"; mv -- "$runtime_unit_tmp" "$runtime_unit"
  systemctl daemon-reload
  systemctl enable --now "$runtime_service_name.service"
  if ((was_runtime_active)); then
    current_runtime_pid="$(runtime_service_pid "$runtime_service_name.service")"
    if [[ "$current_runtime_pid" =~ ^[1-9][0-9]*$ && "$current_runtime_pid" != "$old_runtime_pid" ]]; then
      wait_for_runtime_generation "$runtime_service_name.service" "$runtime_socket" "$old_runtime_pid" || \
        die "$runtime_service_name.service did not finish its in-progress upgrade"
    elif runtime_upgrade_result="$(runuser --user "$PANEL_USER" -- env \
      MCPANEL_DATA_DIR="$data_dir" MCPANEL_CONFIG_DIR="$config_dir" \
      "$install_dir/McPanel.Api" --mcpanel-runtime-upgrade-when-idle)"; then
      case "$runtime_upgrade_result" in
        restarting)
          wait_for_runtime_generation "$runtime_service_name.service" "$runtime_socket" "$old_runtime_pid" || \
            die "$runtime_service_name.service did not restart onto the updated binary"
          ;;
        busy)
          wait_for_runtime_ready "$runtime_service_name.service" "$runtime_socket" || \
            die "$runtime_service_name.service did not remain ready"
          ;;
        *) die "$runtime_service_name.service returned an invalid upgrade response" ;;
      esac
    else
      wait_for_runtime_generation "$runtime_service_name.service" "$runtime_socket" "$old_runtime_pid" || \
        die "could not ask $runtime_service_name.service to upgrade safely"
    fi
  else
    wait_for_runtime_ready "$runtime_service_name.service" "$runtime_socket" || \
      die "$runtime_service_name.service did not become ready"
  fi

  if ((was_active)); then
    systemctl start "$service_name.service"
    wait_for_active "$service_name.service" || die "$service_name.service did not remain active"
    wait_for_http "$config_dir" || die "the panel HTTP endpoint did not become ready"
  fi
  update_succeeded=1
  local successful_backup
  for successful_backup in "$old_unit_backup" "$old_runtime_unit_backup" "$old_memory_dropin_backup"; do
    if [[ -n "$successful_backup" && -f "$successful_backup" ]]; then rm -f -- "$successful_backup"; fi
  done
  trap - EXIT
  info "MC Panel binaries were updated successfully."
  if ((was_active)); then info "The panel service is active."; else info "The panel service was left stopped."; fi
  info "Previous binaries were retained at $rollback_dir."
  info "Configuration and data were not modified."
}

root_uninstall() {
  require_root
  local install_dir="$1" config_dir="$2" data_dir="$3" service_name="$4" purge="$5"
  local service_unit runtime_service_name runtime_unit memory_dropin_dir memory_dropin managed_path

  require_commands getent groupdel realpath rm rmdir systemctl userdel
  validate_service_name "$service_name"
  install_dir="$(normalize_managed_path "$install_dir")"
  config_dir="$(normalize_managed_path "$config_dir")"
  data_dir="$(normalize_managed_path "$data_dir")"
  validate_managed_paths "$install_dir" "$config_dir" "$data_dir"
  for managed_path in "$install_dir" "$config_dir" "$data_dir"; do
    [[ ! -L "$managed_path" ]] || die "refusing symbolic-link managed path: $managed_path"
  done
  if [[ -e "$install_dir" ]]; then
    [[ -d "$install_dir" ]] || die "install path is not a directory"
    [[ -f "$install_dir/McPanel.Api" && ! -L "$install_dir/McPanel.Api" ]] || die "install directory does not contain MC Panel"
  fi

  service_unit="/etc/systemd/system/$service_name.service"
  runtime_service_name="$service_name-runtime"
  runtime_unit="/etc/systemd/system/$runtime_service_name.service"
  memory_dropin_dir="/etc/systemd/system/$service_name.service.d"
  memory_dropin="$memory_dropin_dir/50-mcpanel-memory.conf"
  for managed_path in "$service_unit" "$runtime_unit"; do
    [[ ! -e "$managed_path" || -f "$managed_path" && ! -L "$managed_path" ]] || die "unsafe unit file: $managed_path"
  done

  systemctl disable --now "$service_name.service" >/dev/null 2>&1 || true
  systemctl disable --now "$runtime_service_name.service" >/dev/null 2>&1 || true
  rm -f -- "$service_unit" "$runtime_unit"
  if [[ -e "$memory_dropin" ]]; then
    [[ -f "$memory_dropin" && ! -L "$memory_dropin" ]] || die "unsafe memory drop-in"
    rm -f -- "$memory_dropin"
    rmdir --ignore-fail-on-non-empty -- "$memory_dropin_dir" 2>/dev/null || true
  fi
  systemctl daemon-reload
  systemctl reset-failed "$service_name.service" >/dev/null 2>&1 || true
  systemctl reset-failed "$runtime_service_name.service" >/dev/null 2>&1 || true
  if [[ -d "$install_dir" ]]; then rm -rf --one-file-system -- "$install_dir"; fi

  if ((purge)); then
    info "Permanently deleting $config_dir and $data_dir."
    if [[ -d "$config_dir" ]]; then rm -rf --one-file-system -- "$config_dir"; fi
    if [[ -d "$data_dir" ]]; then rm -rf --one-file-system -- "$data_dir"; fi
    if getent passwd "$PANEL_USER" >/dev/null; then userdel "$PANEL_USER"; fi
    if getent group "$PANEL_GROUP" >/dev/null; then
      groupdel "$PANEL_GROUP" 2>/dev/null || warn "group $PANEL_GROUP is still in use and was retained"
    fi
    info "MC Panel binaries, configuration, and data were removed."
  else
    info "MC Panel binaries and systemd units were removed."
    info "Preserved configuration: $config_dir"
    info "Preserved instances, worlds, databases, keys, logs, and backups: $data_dir"
    info "The $PANEL_USER account was retained."
  fi
  info "Review dated rollback directories beside $install_dir separately."
}

root_import_server() {
  require_root
  local raw_source="$1" install_dir="$2" config_dir="$3" data_dir="$4" service_name="$5"
  shift 5
  local -a import_options=("$@")
  local -a panel_environment=()
  local source stage_dir="" content result_file="" source_label json_result=""
  local environment_file environment_line environment_key environment_value
  local service_unit panel_was_active=0 panel_stopped=0 dry_run=0 json=0 import_rc=0 restart_rc=0
  local option

  for option in "${import_options[@]}"; do
    [[ "$option" == "--dry-run" ]] && dry_run=1
    [[ "$option" == "--json" ]] && json=1
  done

  import_cleanup() {
    local rc=$?
    set +e
    if ((panel_stopped && panel_was_active)); then
      systemctl start "$service_name.service" >/dev/null 2>&1 || true
    fi
    if [[ -n "$stage_dir" && -d "$stage_dir" ]]; then rm -rf -- "$stage_dir"; fi
    trap - EXIT INT TERM
    exit "$rc"
  }
  trap import_cleanup EXIT
  trap 'exit 2' INT TERM

  require_commands basename cat chown curl env find getent grep mktemp realpath rm runuser sleep systemctl
  validate_service_name "$service_name"
  install_dir="$(normalize_managed_path "$install_dir")"
  config_dir="$(normalize_managed_path "$config_dir")"
  data_dir="$(normalize_managed_path "$data_dir")"
  validate_managed_paths "$install_dir" "$config_dir" "$data_dir"
  [[ -d "$install_dir" && ! -L "$install_dir" ]] || die "installation is missing or unsafe: $install_dir"
  [[ -x "$install_dir/McPanel.Api" && ! -L "$install_dir/McPanel.Api" ]] || die "current executable is missing"
  [[ -d "$data_dir/staging" && ! -L "$data_dir/staging" ]] || die "panel staging directory is missing or unsafe"
  [[ ! -L "$raw_source" ]] || die "import source must not be a symbolic link"
  source="$(realpath -e -- "$raw_source" 2>/dev/null)" || \
    die_import "$json" 3 IMPORT_SOURCE_NOT_FOUND "import source no longer exists"
  [[ -d "$source" || -f "$source" ]] || die "import source must be a directory or regular archive"
  if [[ -d "$source" ]] && find -P "$source" \( -type l -o \! -type d \! -type f \) -print -quit | grep -q .; then
    die_import "$json" 3 IMPORT_SPECIAL_FILE "import source contains a symbolic link or special file"
  fi
  if [[ -d "$source" ]] && find -P "$source" -type f -links +1 -print -quit | grep -q .; then
    die_import "$json" 3 IMPORT_HARD_LINK "import source contains a hard-linked file"
  fi
  if [[ -d "$source" && ( "$data_dir" == "$source" || "$data_dir" == "$source/"* ) ]]; then
    die "import source must not contain the panel data directory"
  fi
  service_unit="/etc/systemd/system/$service_name.service"
  [[ -f "$service_unit" && ! -L "$service_unit" ]] || die "systemd unit is missing or unsafe: $service_unit"
  environment_file="$config_dir/mcpanel.env"
  [[ -f "$environment_file" && ! -L "$environment_file" ]] || die "panel environment file is missing or unsafe: $environment_file"
  while IFS= read -r environment_line || [[ -n "$environment_line" ]]; do
    [[ -z "$environment_line" || "$environment_line" == \#* ]] && continue
    environment_key="${environment_line%%=*}"
    environment_value="${environment_line#*=}"
    [[ "$environment_key" != "$environment_line" && "$environment_key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || \
      die "panel environment file contains an invalid assignment"
    if [[ "$environment_value" == \"*\" && ${#environment_value} -ge 2 ]] ||
       [[ "$environment_value" == \'*\' && ${#environment_value} -ge 2 ]]; then
      environment_value="${environment_value:1:${#environment_value}-2}"
    fi
    panel_environment+=("$environment_key=$environment_value")
  done < "$environment_file"
  getent passwd "$PANEL_USER" >/dev/null || die "$PANEL_USER service account does not exist"

  stage_dir="$(mktemp -d "$data_dir/staging/import-cli.XXXXXX")"
  content="$stage_dir/content"
  result_file="$stage_dir/result.json"
  source_label="$(basename -- "$source")"
  set +e
  if ((json)); then
    "$install_dir/McPanel.Api" --mcpanel-import-stage "$source" "$content" --json > "$result_file"
  else
    "$install_dir/McPanel.Api" --mcpanel-import-stage "$source" "$content"
  fi
  import_rc=$?
  set -e
  if ((import_rc != 0)); then
    if ((json)); then
      if [[ -s "$result_file" ]]; then cat -- "$result_file"
      else printf '{"ok":false,"code":"IMPORT_STAGE_FAILED","message":"The import source could not be staged."}\n'
      fi
    fi
    return "$import_rc"
  fi
  chown -R "$PANEL_USER:$PANEL_GROUP" "$stage_dir"

  if ((!dry_run)) && systemctl is-active --quiet "$service_name.service"; then
    panel_was_active=1
    systemctl stop "$service_name.service"
    panel_stopped=1
  fi

  set +e
  if ((json)); then
    runuser --user "$PANEL_USER" -- env "${panel_environment[@]}" \
      MCPANEL_DATA_DIR="$data_dir" MCPANEL_CONFIG_DIR="$config_dir" \
      "$install_dir/McPanel.Api" --mcpanel-import-server "$content" \
      --source-label "$source_label" "${import_options[@]}" > "$result_file"
  else
    runuser --user "$PANEL_USER" -- env "${panel_environment[@]}" \
      MCPANEL_DATA_DIR="$data_dir" MCPANEL_CONFIG_DIR="$config_dir" \
      "$install_dir/McPanel.Api" --mcpanel-import-server "$content" \
      --source-label "$source_label" "${import_options[@]}"
  fi
  import_rc=$?
  set -e
  if ((json)) && [[ -s "$result_file" ]]; then json_result="$(cat -- "$result_file")"; fi

  if ((panel_stopped && panel_was_active)); then
    if ! systemctl start "$service_name.service" ||
       ! wait_for_active "$service_name.service" ||
       ! wait_for_http "$config_dir"; then
      restart_rc=5
    else
      panel_stopped=0
    fi
  fi

  if ((json)); then
    if ((restart_rc != 0)); then
      printf '{"ok":false,"code":"IMPORT_PANEL_RESTART_FAILED","message":"The import command finished, but the web panel did not become ready."}\n'
    elif [[ -n "$json_result" ]]; then
      printf '%s\n' "$json_result"
    else
      printf '{"ok":false,"code":"IMPORT_FAILED","message":"The import command did not return a result."}\n'
      import_rc=5
    fi
  elif ((restart_rc != 0)); then
    warn "the import command finished, but the web panel did not become ready"
  fi

  if ((restart_rc != 0)); then return "$restart_rc"; fi
  return "$import_rc"
}

build_for_system_command() {
  local action="$1" install_dir="$2" config_dir="$3" data_dir="$4" service_name="$5"
  local listen_address="${6:-}" port="${7:-}"
  local build_root artifact rid
  require_regular_user
  require_passwordless_sudo
  rid="$(detect_rid)"
  build_root="$(mktemp -d /tmp/mcpanel-system-build.XXXXXX)"
  cleanup_system_build() { local rc=$?; rm -rf -- "$build_root"; trap - EXIT; exit "$rc"; }
  trap cleanup_system_build EXIT
  artifact="$build_root/artifact"
  publish_artifact "$rid" "$artifact"
  if [[ "$action" == "install" ]]; then
    sudo -n -- "$script_path" __install "$artifact" "$install_dir" "$config_dir" "$data_dir" "$service_name" "$listen_address" "$port"
  else
    sudo -n -- "$script_path" __update "$artifact" "$install_dir" "$config_dir" "$data_dir" "$service_name"
  fi
  rm -rf -- "$build_root"
  trap - EXIT
}

invoke_prepared_installer() {
  local installer="$1"
  shift
  "$installer" __apply-prepared "$@"
}

run_remote_system_command() {
  local action="$1" release="$2" install_dir="$3" config_dir="$4" data_dir="$5" service_name="$6"
  local listen_address="${7:-}" port="${8:-}"
  local work_root prepared_dir rid commit installer artifact

  require_regular_user
  require_passwordless_sudo
  require_commands basename chmod curl find grep mkdir mktemp realpath sha256sum tar tr uname
  validate_release_ref "$release"
  rid="$(detect_rid)"
  work_root="$(mktemp -d /tmp/mcpanel-release.XXXXXX)"
  cleanup_remote_system_command() {
    local rc=$?
    rm -rf -- "$work_root"
    trap - EXIT
    exit "$rc"
  }
  trap cleanup_remote_system_command EXIT

  info "Downloading MC Panel release $release for $rid."
  prepared_dir="$(prepare_remote_release "$release" "$rid" "$work_root")" || \
    die "could not download a consistent MC Panel release after three attempts"
  commit="$(tr -d '\r\n' < "$prepared_dir/commit")"
  installer="$prepared_dir/mcpanel-$commit.sh"
  artifact="$prepared_dir/artifact"
  invoke_prepared_installer "$installer" "$action" "$artifact" "$release" "$commit" "$rid" \
    "$install_dir" "$config_dir" "$data_dir" "$service_name" "$listen_address" "$port"
  rm -rf -- "$work_root"
  trap - EXIT
}

apply_prepared_system_command() {
  local action="$1" artifact="$2" release="$3" commit="$4" rid="$5"
  local install_dir="$6" config_dir="$7" data_dir="$8" service_name="$9"
  local listen_address="${10}" port="${11}"

  require_regular_user
  require_passwordless_sudo
  validate_release_ref "$release"
  [[ "$commit" =~ ^[a-f0-9]{40}$ ]] || die "invalid prepared release commit"
  validate_rid "$rid"
  artifact="$(realpath -e -- "$artifact")"
  validate_artifact "$artifact"
  validate_release_metadata "$artifact" "$release" "$commit" "$rid"
  case "$action" in
    install)
      sudo -n -- "$script_path" __install "$artifact" "$install_dir" "$config_dir" "$data_dir" \
        "$service_name" "$listen_address" "$port"
      ;;
    update)
      sudo -n -- "$script_path" __update "$artifact" "$install_dir" "$config_dir" "$data_dir" "$service_name"
      ;;
    *) die "invalid prepared release action: $action" ;;
  esac
}

command_install() {
  local install_dir="$DEFAULT_INSTALL_DIR" config_dir="$DEFAULT_CONFIG_DIR" data_dir="$DEFAULT_DATA_DIR"
  local service_name="$DEFAULT_SERVICE_NAME" listen_address="0.0.0.0" port="$DEFAULT_PORT"
  local source="$DEFAULT_INSTALL_SOURCE" release="$DEFAULT_RELEASE" release_set=0
  while (($#)); do
    case "$1" in
      --source) (($# >= 2)) || die "$1 requires a value"; source="$2"; shift 2 ;;
      --release) (($# >= 2)) || die "$1 requires a value"; release="$2"; release_set=1; shift 2 ;;
      --listen-address) (($# >= 2)) || die "$1 requires a value"; listen_address="$2"; shift 2 ;;
      --port) (($# >= 2)) || die "$1 requires a value"; port="$2"; shift 2 ;;
      --install-dir) (($# >= 2)) || die "$1 requires a value"; install_dir="$2"; shift 2 ;;
      --config-dir) (($# >= 2)) || die "$1 requires a value"; config_dir="$2"; shift 2 ;;
      --data-dir) (($# >= 2)) || die "$1 requires a value"; data_dir="$2"; shift 2 ;;
      --service-name) (($# >= 2)) || die "$1 requires a value"; service_name="$2"; shift 2 ;;
      -h|--help) usage; return ;;
      *) die "unknown install option: $1" ;;
    esac
  done
  validate_install_source "$source"
  if [[ "$source" == "local" ]]; then
    ((!release_set)) || die "--release cannot be used with --source local"
    build_for_system_command install "$install_dir" "$config_dir" "$data_dir" "$service_name" "$listen_address" "$port"
  else
    validate_release_ref "$release"
    run_remote_system_command install "$release" "$install_dir" "$config_dir" "$data_dir" "$service_name" "$listen_address" "$port"
  fi
}

command_update() {
  local install_dir="$DEFAULT_INSTALL_DIR" config_dir="$DEFAULT_CONFIG_DIR" data_dir="$DEFAULT_DATA_DIR" service_name="$DEFAULT_SERVICE_NAME"
  local source="$DEFAULT_INSTALL_SOURCE" release="$DEFAULT_RELEASE" release_set=0
  while (($#)); do
    case "$1" in
      --source) (($# >= 2)) || die "$1 requires a value"; source="$2"; shift 2 ;;
      --release) (($# >= 2)) || die "$1 requires a value"; release="$2"; release_set=1; shift 2 ;;
      --install-dir) (($# >= 2)) || die "$1 requires a value"; install_dir="$2"; shift 2 ;;
      --config-dir) (($# >= 2)) || die "$1 requires a value"; config_dir="$2"; shift 2 ;;
      --data-dir) (($# >= 2)) || die "$1 requires a value"; data_dir="$2"; shift 2 ;;
      --service-name) (($# >= 2)) || die "$1 requires a value"; service_name="$2"; shift 2 ;;
      -h|--help) usage; return ;;
      *) die "unknown update option: $1" ;;
    esac
  done
  validate_install_source "$source"
  if [[ "$source" == "local" ]]; then
    ((!release_set)) || die "--release cannot be used with --source local"
    build_for_system_command update "$install_dir" "$config_dir" "$data_dir" "$service_name"
  else
    validate_release_ref "$release"
    run_remote_system_command update "$release" "$install_dir" "$config_dir" "$data_dir" "$service_name"
  fi
}

command_import_server() {
  local install_dir="$DEFAULT_INSTALL_DIR" config_dir="$DEFAULT_CONFIG_DIR" data_dir="$DEFAULT_DATA_DIR"
  local service_name="$DEFAULT_SERVICE_NAME" source="" json=0 argument
  local -a import_options=()
  for argument in "$@"; do [[ "$argument" == "--json" ]] && json=1; done
  if (($#)) && [[ "$1" != --* ]]; then source="$1"; shift; fi
  while (($#)); do
    case "$1" in
      --install-dir) (($# >= 2)) || die_import "$json" 2 IMPORT_USAGE "$1 requires a value"; install_dir="$2"; shift 2 ;;
      --config-dir) (($# >= 2)) || die_import "$json" 2 IMPORT_USAGE "$1 requires a value"; config_dir="$2"; shift 2 ;;
      --data-dir) (($# >= 2)) || die_import "$json" 2 IMPORT_USAGE "$1 requires a value"; data_dir="$2"; shift 2 ;;
      --service-name) (($# >= 2)) || die_import "$json" 2 IMPORT_USAGE "$1 requires a value"; service_name="$2"; shift 2 ;;
      --name|--kind|--version|--loader-version|--launch-target|--java-runtime|--memory-mb|--port|--jvm-args)
        (($# >= 2)) || die_import "$json" 2 IMPORT_USAGE "$1 requires a value"
        import_options+=("$1" "$2")
        shift 2
        ;;
      --accept-eula|--non-interactive|--dry-run|--json)
        import_options+=("$1")
        shift
        ;;
      -h|--help) usage; return ;;
      --*) die_import "$json" 2 IMPORT_USAGE "unknown import option: $1" ;;
      *) [[ -z "$source" ]] || die_import "$json" 2 IMPORT_USAGE "only one import source may be supplied"; source="$1"; shift ;;
    esac
  done
  [[ -n "$source" ]] || die_import "$json" 2 IMPORT_USAGE "import-server requires a source directory or archive"
  require_regular_user
  require_passwordless_sudo
  require_commands realpath
  [[ ! -L "$source" ]] || die_import "$json" 3 IMPORT_SYMBOLIC_LINK "import source must not be a symbolic link"
  source="$(realpath -e -- "$source" 2>/dev/null)" || die_import "$json" 3 IMPORT_SOURCE_NOT_FOUND "import source does not exist"
  [[ -d "$source" || -f "$source" ]] || die_import "$json" 3 IMPORT_SOURCE_INVALID "import source must be a directory or regular archive"
  sudo -n -- "$script_path" __import-server "$source" "$install_dir" "$config_dir" "$data_dir" "$service_name" "${import_options[@]}"
}

command_remove() {
  local purge="$1"; shift
  local install_dir="$DEFAULT_INSTALL_DIR" config_dir="$DEFAULT_CONFIG_DIR" data_dir="$DEFAULT_DATA_DIR" service_name="$DEFAULT_SERVICE_NAME"
  local purge_confirmed=0
  while (($#)); do
    case "$1" in
      --install-dir) (($# >= 2)) || die "$1 requires a value"; install_dir="$2"; shift 2 ;;
      --config-dir) (($# >= 2)) || die "$1 requires a value"; config_dir="$2"; shift 2 ;;
      --data-dir) (($# >= 2)) || die "$1 requires a value"; data_dir="$2"; shift 2 ;;
      --service-name) (($# >= 2)) || die "$1 requires a value"; service_name="$2"; shift 2 ;;
      --yes-really-purge) purge_confirmed=1; shift ;;
      -h|--help) usage; return ;;
      *) die "unknown removal option: $1" ;;
    esac
  done
  if ((purge)); then ((purge_confirmed)) || die "purge requires --yes-really-purge"; else ((!purge_confirmed)) || die "--yes-really-purge is only valid with purge"; fi
  require_passwordless_sudo
  sudo -n -- "$script_path" __uninstall "$install_dir" "$config_dir" "$data_dir" "$service_name" "$purge"
}

command_build() {
  local output="" rid=""
  while (($#)); do
    case "$1" in
      --rid) (($# >= 2)) || die "$1 requires a value"; rid="$2"; shift 2 ;;
      -h|--help) usage; return ;;
      --*) die "unknown build option: $1" ;;
      *) [[ -z "$output" ]] || die "only one output directory may be supplied"; output="$1"; shift ;;
    esac
  done
  [[ -n "$output" ]] || die "build requires an output directory"
  if [[ -z "$rid" ]]; then rid="$(detect_rid)"; fi
  publish_artifact "$rid" "$output"
}

command_status() {
  local config_dir="$DEFAULT_CONFIG_DIR" service_name="$DEFAULT_SERVICE_NAME" probe_url="" rc=0
  while (($#)); do
    case "$1" in
      --config-dir) (($# >= 2)) || die "$1 requires a value"; config_dir="$2"; shift 2 ;;
      --service-name) (($# >= 2)) || die "$1 requires a value"; service_name="$2"; shift 2 ;;
      -h|--help) usage; return ;;
      *) die "unknown status option: $1" ;;
    esac
  done
  validate_service_name "$service_name"
  config_dir="$(normalize_managed_path "$config_dir")"
  require_commands curl systemctl
  systemctl --no-pager --full status "$service_name.service" || rc=1
  systemctl --no-pager --full status "$service_name-runtime.service" || rc=1
  require_passwordless_sudo
  probe_url="$(sudo -n -- "$script_path" __probe-url "$config_dir" 2>/dev/null || true)"
  if [[ -n "$probe_url" ]]; then
    if ! curl --noproxy '*' --fail --silent --show-error --max-time 10 "$probe_url"; then rc=1; fi
    printf '\n'
  else
    warn "could not determine ASPNETCORE_URLS from $config_dir/mcpanel.env"
    rc=1
  fi
  return "$rc"
}

if [[ "${MCPANEL_SOURCE_ONLY:-0}" == "1" ]]; then
  # shellcheck disable=SC2317
  return 0 2>/dev/null || exit 0
fi

command="${1:-help}"
if (($#)); then shift; fi
case "$command" in
  install) command_install "$@" ;;
  update) command_update "$@" ;;
  import-server) command_import_server "$@" ;;
  uninstall) command_remove 0 "$@" ;;
  purge) command_remove 1 "$@" ;;
  build) command_build "$@" ;;
  status) command_status "$@" ;;
  help|-h|--help) usage ;;
  __apply-prepared) [[ $# -eq 11 ]] || die "invalid prepared release invocation"; apply_prepared_system_command "$@" ;;
  __install) [[ $# -eq 7 ]] || die "invalid internal install invocation"; root_install "$@" ;;
  __update) [[ $# -eq 5 ]] || die "invalid internal update invocation"; root_update "$@" ;;
  __import-server) (($# >= 5)) || die "invalid internal import invocation"; root_import_server "$@" ;;
  __uninstall) [[ $# -eq 5 ]] || die "invalid internal uninstall invocation"; root_uninstall "$@" ;;
  __probe-url) [[ $# -eq 1 ]] || die "invalid internal probe invocation"; require_root; http_probe_url "$1" ;;
  *) usage >&2; die "unknown command: $command" ;;
esac

#!/usr/bin/env bash

set -Eeuo pipefail

test_root="$(mktemp -d)"
test_script_dir="$(dirname -- "${BASH_SOURCE[0]}")"
test_repo_root="$(realpath -e -- "$test_script_dir/..")"
export MCPANEL_RELEASE_BASE_URL="file://$test_root/releases"
export MCPANEL_SOURCE_ONLY=1
# shellcheck disable=SC1091
source "$test_repo_root/mcpanel.sh"

test_command_path="$test_root/global-bin/mcpanel"
system_manager_command_path() { printf '%s\n' "$test_command_path"; }

cleanup() {
  rm -rf -- "$test_root"
}
trap cleanup EXIT

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

assert_equal() {
  local expected="$1" actual="$2" label="$3"
  [[ "$actual" == "$expected" ]] || fail "$label: expected '$expected', got '$actual'"
}

assert_fails() {
  local label="$1"
  shift
  if ("$@" >/dev/null 2>&1); then fail "$label: command unexpectedly succeeded"; fi
}

write_artifact() {
  local artifact_dir="$1" release="$2" commit="$3" rid="$4"
  mkdir -p -- "$artifact_dir/wwwroot"
  printf '#!/usr/bin/env bash\nexit 0\n' > "$artifact_dir/McPanel.Api"
  chmod 0755 "$artifact_dir/McPanel.Api"
  printf '<!doctype html>\n' > "$artifact_dir/wwwroot/index.html"
  {
    printf 'schema=1\n'
    printf 'release=%s\n' "$release"
    printf 'commit=%s\n' "$commit"
    printf 'rid=%s\n' "$rid"
  } > "$artifact_dir/$RELEASE_METADATA_NAME"
}

create_fixture_release() {
  local release="$1" commit="$2" release_dir
  local installer rid artifact archive
  release_dir="$test_root/releases/$release"
  installer="$release_dir/mcpanel-$commit.sh"
  mkdir -p -- "$release_dir"
  # shellcheck disable=SC2016
  printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$*" > "${MCPANEL_HANDOFF_LOG:-/dev/null}"\n' > "$installer"
  chmod 0755 "$installer"
  for rid in linux-x64 linux-arm64; do
    artifact="$test_root/artifact-$release-$rid"
    archive="$release_dir/mcpanel-$commit-$rid.tar.gz"
    write_artifact "$artifact" "$release" "$commit" "$rid"
    tar --create --gzip --file "$archive" --directory "$artifact" .
  done
  {
    printf 'schema=1\n'
    printf 'commit=%s\n' "$commit"
    printf 'script_sha256=%s\n' "$(sha256sum --binary -- "$installer" | awk '{print $1}')"
    printf 'linux_x64_sha256=%s\n' "$(sha256sum --binary -- "$release_dir/mcpanel-$commit-linux-x64.tar.gz" | awk '{print $1}')"
    printf 'linux_arm64_sha256=%s\n' "$(sha256sum --binary -- "$release_dir/mcpanel-$commit-linux-arm64.tar.gz" | awk '{print $1}')"
  } > "$release_dir/$RELEASE_MANIFEST_NAME"
}

test_option_parsing() {
  run_remote_system_command() { printf 'remote|%s\n' "$*"; }
  build_for_system_command() { printf 'local|%s\n' "$*"; }

  assert_equal \
    "remote|install main /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel 0.0.0.0 6050" \
    "$(command_install)" \
    "install defaults"
  assert_equal \
    "remote|update v1.2.3 /srv/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel" \
    "$(command_update --release v1.2.3 --install-dir /srv/mcpanel)" \
    "update release selection"
  assert_equal \
    "local|update /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel" \
    "$(command_update --source local)" \
    "local source selection"
  assert_fails "unknown source" command_install --source archive
  assert_fails "release with local source" command_update --source local --release main
  assert_fails "unsafe release tag" command_update --release feature/main
}

test_rid_detection() {
  # shellcheck disable=SC2329
  assert_equal "linux-x64" "$(uname() { printf 'x86_64\n'; }; detect_rid)" "x64 detection"
  # shellcheck disable=SC2329
  assert_equal "linux-arm64" "$(uname() { printf 'aarch64\n'; }; detect_rid)" "ARM64 detection"
  assert_fails "unsupported architecture" bash -c \
    'export MCPANEL_SOURCE_ONLY=1; source ./mcpanel.sh; uname() { printf "riscv64\n"; }; detect_rid'
}

test_release_validation() {
  local commit="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" prepared
  create_fixture_release main "$commit"
  prepared="$(prepare_remote_release main linux-x64 "$test_root/valid-work")" || fail "valid release was rejected"
  [[ -f "$prepared/artifact/McPanel.Api" ]] || fail "prepared artifact is missing the executable"
  [[ "$(< "$prepared/commit")" == "$commit" ]] || fail "prepared commit was not recorded"

  cp -a -- "$test_root/releases/main" "$test_root/releases/malformed"
  printf 'schema=2\n' > "$test_root/releases/malformed/$RELEASE_MANIFEST_NAME"
  assert_fails "malformed manifest" prepare_remote_release_attempt malformed linux-x64 "$test_root/malformed-work"

  cp -a -- "$test_root/releases/main" "$test_root/releases/bad-checksum"
  sed -i 's/^linux_x64_sha256=.*/linux_x64_sha256=0000000000000000000000000000000000000000000000000000000000000000/' \
    "$test_root/releases/bad-checksum/$RELEASE_MANIFEST_NAME"
  assert_fails "checksum mismatch" prepare_remote_release_attempt bad-checksum linux-x64 "$test_root/checksum-work"

  cp -a -- "$test_root/releases/main" "$test_root/releases/missing"
  rm -- "$test_root/releases/missing/mcpanel-$commit-linux-x64.tar.gz"
  assert_fails "missing asset" prepare_remote_release_attempt missing linux-x64 "$test_root/missing-work"
}

test_unsafe_archive() {
  local release="unsafe" commit="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  local release_dir="$test_root/releases/$release" payload="$test_root/unsafe-payload"
  local installer archive
  create_fixture_release "$release" "$commit"
  installer="$release_dir/mcpanel-$commit.sh"
  archive="$release_dir/mcpanel-$commit-linux-x64.tar.gz"
  mkdir -p -- "$payload"
  printf 'bad\n' > "$payload/file"
  tar --create --gzip --file "$archive" --directory "$test_root" \
    --transform='s|^unsafe-payload|../escape|' unsafe-payload
  {
    printf 'schema=1\ncommit=%s\n' "$commit"
    printf 'script_sha256=%s\n' "$(sha256sum --binary -- "$installer" | awk '{print $1}')"
    printf 'linux_x64_sha256=%s\n' "$(sha256sum --binary -- "$archive" | awk '{print $1}')"
    printf 'linux_arm64_sha256=%s\n' "$(sha256sum --binary -- "$release_dir/mcpanel-$commit-linux-arm64.tar.gz" | awk '{print $1}')"
  } > "$release_dir/$RELEASE_MANIFEST_NAME"
  assert_fails "parent path in archive" prepare_remote_release_attempt "$release" linux-x64 "$test_root/unsafe-work"
}

test_retry_and_handoff() {
  local release="retry" commit="cccccccccccccccccccccccccccccccccccccccc"
  local marker="$test_root/first-download" prepared handoff_log="$test_root/handoff.log"
  local original_download_definition actual_release="self-refresh"
  local actual_commit="ffffffffffffffffffffffffffffffffffffffff" actual_dir actual_prepared
  local fake_bin="$test_root/fake-bin" sudo_log="$test_root/sudo.log" current_path="$PATH"
  create_fixture_release "$release" "$commit"
  original_download_definition="$(declare -f download_release_asset)"
  download_release_asset() {
    local requested_release="$1" asset_name="$2" destination="$3"
    if [[ "$asset_name" == "$RELEASE_MANIFEST_NAME" && ! -e "$marker" ]]; then
      : > "$marker"
      printf 'incomplete\n' > "$destination"
      return 0
    fi
    curl --fail --silent --show-error --output "$destination" \
      "$GITHUB_RELEASE_BASE_URL/$requested_release/$asset_name"
  }
  prepared="$(prepare_remote_release "$release" linux-x64 "$test_root/retry-work")" || fail "release retry did not recover"
  [[ "$prepared" == "$test_root/retry-work/attempt-2" ]] || fail "release retry did not use the second attempt"
  eval "$original_download_definition"

  export MCPANEL_HANDOFF_LOG="$handoff_log"
  invoke_prepared_installer "$prepared/mcpanel-$commit.sh" update "$prepared/artifact" "$release" "$commit" linux-x64 \
    /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel '' '' tester
  [[ "$(< "$handoff_log")" == "__apply-prepared update $prepared/artifact $release $commit linux-x64 /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel   tester" ]] || \
    fail "refreshed installer handoff arguments were incorrect"
  [[ "$(wc -l < "$handoff_log")" -eq 1 ]] || fail "refreshed installer ran more than once"

  create_fixture_release "$actual_release" "$actual_commit"
  actual_dir="$test_root/releases/$actual_release"
  cp -- "$test_repo_root/mcpanel.sh" "$actual_dir/mcpanel-$actual_commit.sh"
  chmod 0755 "$actual_dir/mcpanel-$actual_commit.sh"
  sed -i "s/^script_sha256=.*/script_sha256=$(sha256sum --binary -- "$actual_dir/mcpanel-$actual_commit.sh" | awk '{print $1}')/" \
    "$actual_dir/$RELEASE_MANIFEST_NAME"
  actual_prepared="$(prepare_remote_release "$actual_release" linux-x64 "$test_root/self-refresh-work")" || \
    fail "the real refreshed installer could not be prepared"
  mkdir -p -- "$fake_bin"
  # shellcheck disable=SC2016
  printf '#!/usr/bin/env bash\nif [[ "$*" == "-n true" ]]; then exit 0; fi\nprintf "%%s\\n" "$*" >> "$MCPANEL_SUDO_LOG"\n' > "$fake_bin/sudo"
  chmod 0755 "$fake_bin/sudo"
  export MCPANEL_SUDO_LOG="$sudo_log"
  export PATH="$fake_bin:$PATH"
  MCPANEL_SOURCE_ONLY=0 invoke_prepared_installer "$actual_prepared/mcpanel-$actual_commit.sh" update \
    "$actual_prepared/artifact" "$actual_release" "$actual_commit" linux-x64 \
    /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel '' '' tester
  export PATH="$current_path"
  [[ "$(wc -l < "$sudo_log")" -eq 1 ]] || fail "the refreshed installer did not perform one privileged handoff"
  [[ "$(< "$sudo_log")" == "-n -- $actual_prepared/mcpanel-$actual_commit.sh __update $actual_prepared/artifact /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel tester" ]] || \
    fail "the refreshed installer did not apply the prepared artifact"
}

test_release_identity() {
  local commit="dddddddddddddddddddddddddddddddddddddddd"
  write_artifact "$test_root/incoming" main "$commit" linux-x64
  write_artifact "$test_root/installed" main "$commit" linux-x64
  installed_release_matches "$test_root/incoming" "$test_root/installed" || fail "matching release was not detected"
  sed -i 's/dddddddddddddddddddddddddddddddddddddddd/eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee/' \
    "$test_root/installed/$RELEASE_METADATA_NAME"
  if installed_release_matches "$test_root/incoming" "$test_root/installed"; then fail "different releases matched"; fi
}

test_import_option_parsing() {
  local source="$test_root/import-source" output
  mkdir -p -- "$source"
  (
    require_regular_user() { :; }
    require_sudo_access() { :; }
    require_commands() { :; }
    sudo() { printf 'sudo|%s\n' "$*"; }
    output="$(command_import_server "$source" \
      --name "Imported world" --kind paper --version 1.21.8 \
      --launch-target paper.jar --java-runtime /usr/bin/java \
      --memory-mb 4096 --port 25570 --jvm-args "-Dfixture=true" \
      --accept-eula --non-interactive --json \
      --install-dir /srv/mcpanel --config-dir /srv/mcpanel-config \
      --data-dir /srv/mcpanel-data --service-name panel-test)"
    assert_equal \
      "sudo|-n -- $script_path __import-server $source /srv/mcpanel /srv/mcpanel-config /srv/mcpanel-data panel-test --name Imported world --kind paper --version 1.21.8 --launch-target paper.jar --java-runtime /usr/bin/java --memory-mb 4096 --port 25570 --jvm-args -Dfixture=true --accept-eula --non-interactive --json" \
      "$output" \
      "import handoff"
    assert_fails "unknown import option" command_import_server "$source" --unknown
    assert_fails "multiple import sources" command_import_server "$source" "$test_root"
  )
}

test_import_wrapper_flag_detection() {
  assert_equal "0 0" \
    "$(detect_import_wrapper_flags --jvm-args --dry-run --name --json)" \
    "import flags used as option values"
  assert_equal "1 1" \
    "$(detect_import_wrapper_flags --accept-eula --dry-run --json)" \
    "standalone import wrapper flags"
}

test_import_restart_json() {
  local result='{"ok":true,"serverId":"server-123","name":"Imported world","instanceDirectory":"/var/lib/mcpanel/instances/server-123"}'
  assert_equal \
    '{"ok":false,"code":"IMPORT_PANEL_RESTART_FAILED","message":"The import command finished, but the web panel did not become ready.","committed":true,"importResult":{"ok":true,"serverId":"server-123","name":"Imported world","instanceDirectory":"/var/lib/mcpanel/instances/server-123"}}' \
    "$(print_import_json_result "$result" 0 5)" \
    "restart failure preserves committed import details"
}

test_import_stops_panel_after_validation() {
  local fixture="$test_root/coordinated-import" install_dir config_dir data_dir source unit stop_marker output
  install_dir="$fixture/install"
  config_dir="$fixture/config"
  data_dir="$fixture/data"
  source="$fixture/source"
  unit="$fixture/mcpanel.service"
  stop_marker="$fixture/panel-stopped"
  mkdir -p -- "$install_dir" "$config_dir" "$data_dir/staging" "$source"
  printf 'ASPNETCORE_URLS=http://0.0.0.0:6050\n' > "$config_dir/mcpanel.env"
  printf '[Service]\n' > "$unit"
  printf 'server-port=25565\n' > "$source/server.properties"
  # shellcheck disable=SC2016
  printf '#!/usr/bin/env bash\nif [[ "$1" == "--mcpanel-import-stage" ]]; then mkdir -p -- "$3"; exit 0; fi\nexit 99\n' \
    > "$install_dir/McPanel.Api"
  chmod 0755 "$install_dir/McPanel.Api"

  (
    require_root() { :; }
    require_commands() { :; }
    systemd_service_unit() { printf '%s\n' "$unit"; }
    getent() { [[ "$1" == "passwd" && "$2" == "$PANEL_USER" ]] && printf '%s:x:999:999::/nonexistent:/usr/sbin/nologin\n' "$PANEL_USER"; }
    # shellcheck disable=SC2032
    chown() { :; }
    wait_for_active() { :; }
    wait_for_http() { :; }
    systemctl() {
      case "$1" in
        is-active) return 0 ;;
        stop)
          find "$data_dir/staging" -name import-ready -type f -print -quit | grep -q . || return 1
          : > "$stop_marker"
          ;;
        start) return 0 ;;
      esac
    }
    runuser() {
      local argument ready="" proceed="" attempt
      for argument in "$@"; do
        case "$argument" in
          MCPANEL_IMPORT_READY_FILE=*) ready="${argument#*=}" ;;
          MCPANEL_IMPORT_CONTINUE_FILE=*) proceed="${argument#*=}" ;;
        esac
      done
      [[ -n "$ready" && -n "$proceed" ]] || return 90
      : > "$ready"
      for attempt in {1..200}; do [[ -f "$proceed" ]] && break; sleep 0.01; done
      [[ -f "$proceed" && -f "$stop_marker" ]] || return 91
      printf '{"ok":true,"serverId":"server-123"}\n'
    }

    output="$(root_import_server "$source" "$install_dir" "$config_dir" "$data_dir" mcpanel \
      --name Imported --kind vanilla --version 1.21.8 --launch-target server.jar \
      --java-runtime /usr/bin/java --memory-mb 2048 --port 25565 --accept-eula --non-interactive --json)"
    assert_equal '{"ok":true,"serverId":"server-123"}' "$output" "coordinated import result"
    [[ -f "$stop_marker" ]] || fail "panel was not stopped for the import commit window"
  )
}

test_runtime_generation_wait() {
  (
    local pid_reads="$test_root/runtime-pid-reads"
    : > "$pid_reads"
    runtime_service_pid() {
      if [[ ! -s "$pid_reads" ]]; then printf 'read\n' >> "$pid_reads"; printf '101\n';
      else printf 'read\n' >> "$pid_reads"; printf '202\n'; fi
    }
    systemctl() { [[ "$1" == "is-active" && "$2" == "--quiet" && "$3" == "mcpanel-runtime.service" ]]; }
    runtime_socket_ready() { [[ "$1" == "/var/lib/mcpanel/runtime/control.sock" ]]; }
    sleep() { :; }

    wait_for_runtime_generation mcpanel-runtime.service /var/lib/mcpanel/runtime/control.sock 101 || \
      fail "runtime generation wait did not observe the replacement process"
    assert_equal "4" "$(wc -l < "$pid_reads")" "runtime generation stability checks"
  )
}

test_service_security_contract() {
  local panel_unit runtime_unit instances_mode_count
  panel_unit="$(render_panel_unit /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel)"
  runtime_unit="$(render_runtime_unit /opt/mcpanel /etc/mcpanel /var/lib/mcpanel)"
  [[ "$panel_unit" == *"LoadCredential=mcpanel.setup-token"* ]] || fail "panel unit does not load the setup credential"
  [[ "$panel_unit" == *'Environment=MCPANEL_SETUP_TOKEN_FILE=%d/mcpanel.setup-token'* ]] || fail "panel unit does not expose the credential file"
  [[ "$panel_unit" == *"UMask=0077"* ]] || fail "panel private umask changed"
  [[ "$panel_unit" != *"RestrictSUIDSGID=yes"* ]] || fail "panel unit blocks regular-instance setgid permission normalization"
  [[ "$runtime_unit" == *"UMask=0007"* ]] || fail "runtime group-writable umask is missing"
  [[ "$runtime_unit" == *"RestrictSUIDSGID=yes"* ]] || fail "runtime setuid/setgid restriction is missing"
  [[ "$runtime_unit" != *"LoadCredential="* ]] || fail "runtime service should not receive the setup credential"
  # Match the literal installer variable, not its value in this test process.
  # shellcheck disable=SC2016
  instances_mode_count="$(grep -c -- '-m 2750 -- "$data_dir/instances"' "$test_repo_root/mcpanel.sh")"
  [[ "$instances_mode_count" -eq 2 ]] || fail "instances parent must be setgid and group-readable, but not group-writable"
  if grep -q 'MCPANEL_SETUP_TOKEN=' "$test_repo_root/mcpanel.sh"; then
    fail "installer still accepts setup tokens from the environment file"
  fi
  if grep -q 'setup_token_file=' "$test_repo_root/mcpanel.sh"; then
    fail "installer still accepts the retired setup-token file"
  fi
}

test_systemd_minimum() {
  (
    # validate_host invokes this test stub indirectly.
    # shellcheck disable=SC2317,SC2329
    systemctl() { printf 'systemd 246 (246.1)\n'; }
    assert_fails "systemd 246" validate_host
  )
  (
    # validate_host invokes this test stub indirectly.
    # shellcheck disable=SC2317,SC2329
    systemctl() { printf 'systemd 247 (247.1)\n'; }
    validate_host >/dev/null || fail "systemd 247 was rejected"
  )
}

test_setup_wizard() {
  local fixture="$test_root/setup-wizard" install_dir="$test_root/setup-wizard/install"
  local config_dir="$test_root/setup-wizard/config" data_dir="$test_root/setup-wizard/data" output
  mkdir -p -- "$fixture"

  output="$({
    require_regular_user() { :; }
    require_commands() { :; }
    validate_host() { :; }
    wizard_open_tty() { :; }
    wizard_close_tty() { :; }
    wizard_prompt() { printf -v "$1" '%s' "$3"; }
    wizard_confirm() { return 0; }
    command_install() { printf 'install|%s\n' "$*"; }
    command_update() { fail "fresh setup selected update"; }
    command_setup --install-dir "$install_dir" --config-dir "$config_dir" --data-dir "$data_dir"
  })"
  [[ "$output" == *"install|--source github --release main --listen-address 0.0.0.0 --port 6050 --install-dir $install_dir --config-dir $config_dir --data-dir $data_dir --service-name mcpanel"* ]] || \
    fail "setup wizard did not pass its default install values"

  output="$({
    require_regular_user() { :; }
    require_commands() { :; }
    validate_host() { :; }
    wizard_open_tty() { :; }
    wizard_close_tty() { :; }
    wizard_prompt() { fail "explicit network settings unexpectedly prompted"; }
    wizard_confirm() { return 0; }
    command_install() { printf 'install|%s\n' "$*"; }
    command_update() { fail "fresh setup selected update"; }
    command_setup --listen-address 192.168.1.20 --port 6500 \
      --install-dir "$install_dir" --config-dir "$config_dir" --data-dir "$data_dir"
  })"
  [[ "$output" == *"--listen-address 192.168.1.20 --port 6500"* ]] || \
    fail "setup wizard did not preserve explicit network settings"

  mkdir -p -- "$install_dir"
  printf '#!/usr/bin/env bash\n' > "$install_dir/McPanel.Api"
  output="$({
    require_regular_user() { :; }
    require_commands() { :; }
    validate_host() { :; }
    wizard_open_tty() { :; }
    wizard_close_tty() { :; }
    wizard_prompt() { fail "existing setup prompted for network settings"; }
    wizard_confirm() { return 0; }
    command_install() { fail "existing setup selected install"; }
    command_update() { printf 'update|%s\n' "$*"; }
    command_setup --install-dir "$install_dir" --config-dir "$config_dir" --data-dir "$data_dir"
  })"
  [[ "$output" == *"update|--source github --release main --install-dir $install_dir --config-dir $config_dir --data-dir $data_dir --service-name mcpanel"* ]] || \
    fail "setup wizard did not select update for an existing installation"

  output="$({
    require_regular_user() { :; }
    require_commands() { :; }
    validate_host() { :; }
    wizard_open_tty() { :; }
    wizard_close_tty() { :; }
    wizard_prompt() { :; }
    wizard_confirm() { return 1; }
    command_install() { fail "cancelled setup performed an install"; }
    command_update() { fail "cancelled setup performed an update"; }
    command_setup --install-dir "$install_dir" --config-dir "$config_dir" --data-dir "$data_dir"
  })"
  [[ "$output" == *"Setup cancelled."* ]] || fail "cancelled setup did not report cancellation"
}

test_sudo_access() {
  (
    local authenticated=0 prompts=0
    require_commands() { :; }
    interactive_terminal_available() { return 0; }
    sudo() {
      if [[ "$1" == "-n" && "$2" == "true" ]]; then ((authenticated)); return; fi
      return 1
    }
    sudo_validate_interactively() { authenticated=1; prompts=$((prompts + 1)); }
    require_sudo_access
    assert_equal "1" "$prompts" "sudo prompt count"
  )
  (
    # shellcheck disable=SC2317,SC2329 # These stubs are invoked through assert_fails.
    require_commands() { :; }
    # shellcheck disable=SC2317,SC2329
    interactive_terminal_available() { return 1; }
    # shellcheck disable=SC2317,SC2329
    sudo() { return 1; }
    assert_fails "headless sudo authentication" require_sudo_access
  )
}

test_system_manager_command() {
  local manager_dir="$test_root/global-bin" target="$test_command_path"
  local updated_manager="$test_root/updated-manager" manager_backup
  mkdir -p -- "$manager_dir"
  (
    install() {
      local -a filtered=()
      while (($#)); do
        case "$1" in
          -o|-g) shift 2 ;;
          *) filtered+=("$1"); shift ;;
        esac
      done
      command install "${filtered[@]}"
    }

    install_system_manager_command "$test_repo_root/mcpanel.sh"
    is_system_manager_file "$target" || fail "global command was not installed with its marker"
    assert_equal "755" "$(stat -c '%a' "$target")" "global command mode"

    manager_backup="$(backup_system_manager_command)"
    cp -- "$test_repo_root/mcpanel.sh" "$updated_manager"
    printf '\n# updated-manager-fixture\n' >> "$updated_manager"
    install_system_manager_command "$updated_manager"
    grep -Fq '# updated-manager-fixture' "$target" || fail "global command was not refreshed"
    restore_system_manager_command "$manager_backup"
    if grep -Fq '# updated-manager-fixture' "$target"; then fail "global command rollback did not restore its backup"; fi
    rm -f -- "$manager_backup"

    printf '#!/usr/bin/env bash\nprintf unrelated\\n\n' > "$target"
    chmod 0755 "$target"
    assert_fails "unrelated global command collision" install_system_manager_command "$test_repo_root/mcpanel.sh"
    remove_system_manager_command >/dev/null
    grep -Fq 'unrelated' "$target" || fail "unrelated global command was removed"

    rm -f -- "$target"
    ln -s -- "$test_repo_root/mcpanel.sh" "$target"
    assert_fails "symbolic-link global command collision" validate_system_manager_target
    rm -f -- "$target"
  )
}

test_global_command_scope() {
  local global_copy="$test_root/standalone-bin/mcpanel" output
  mkdir -p -- "$(dirname -- "$global_copy")"
  cp -- "$test_repo_root/mcpanel.sh" "$global_copy"
  chmod 0755 "$global_copy"
  output="$(MCPANEL_SOURCE_ONLY=0 "$global_copy" help)"
  [[ "$output" != *"build OUTPUT"* ]] || fail "global command advertised checkout-only build support"
  assert_fails "global build command" env MCPANEL_SOURCE_ONLY=0 "$global_copy" build "$test_root/build-output"
  assert_fails "global local-source update" env MCPANEL_SOURCE_ONLY=0 "$global_copy" update --source local
}

test_option_parsing
test_rid_detection
test_release_validation
test_unsafe_archive
test_retry_and_handoff
test_release_identity
test_import_option_parsing
test_import_wrapper_flag_detection
test_import_restart_json
test_import_stops_panel_after_validation
test_runtime_generation_wait
test_service_security_contract
test_systemd_minimum
test_setup_wizard
test_sudo_access
test_system_manager_command
test_global_command_scope
printf 'MC Panel installer tests passed.\n'

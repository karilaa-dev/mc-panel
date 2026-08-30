#!/usr/bin/env bash

set -Eeuo pipefail

test_root="$(mktemp -d)"
test_script_dir="$(dirname -- "${BASH_SOURCE[0]}")"
test_repo_root="$(realpath -e -- "$test_script_dir/..")"
export MCPANEL_RELEASE_BASE_URL="file://$test_root/releases"
export MCPANEL_SOURCE_ONLY=1
# shellcheck disable=SC1091
source "$test_repo_root/mcpanel.sh"

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
    /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel '' ''
  [[ "$(< "$handoff_log")" == "__apply-prepared update $prepared/artifact $release $commit linux-x64 /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel  " ]] || \
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
    /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel '' ''
  export PATH="$current_path"
  [[ "$(wc -l < "$sudo_log")" -eq 1 ]] || fail "the refreshed installer did not perform one privileged handoff"
  [[ "$(< "$sudo_log")" == "-n -- $actual_prepared/mcpanel-$actual_commit.sh __update $actual_prepared/artifact /opt/mcpanel /etc/mcpanel /var/lib/mcpanel mcpanel" ]] || \
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
    require_passwordless_sudo() { :; }
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

test_option_parsing
test_rid_detection
test_release_validation
test_unsafe_archive
test_retry_and_handoff
test_release_identity
test_import_option_parsing
test_runtime_generation_wait
printf 'MC Panel installer tests passed.\n'

#!/usr/bin/env bash

set -Eeuo pipefail

test_root="$(mktemp -d)"
test_script_dir="$(dirname -- "${BASH_SOURCE[0]}")"
test_repo_root="$(realpath -e -- "$test_script_dir/..")"

cleanup() {
  rm -rf -- "$test_root"
}
trap cleanup EXIT

fail() {
  printf 'FAIL: %s\n' "$*" >&2
  exit 1
}

assert_fails() {
  local label="$1"
  shift
  if ("$@" >/dev/null 2>&1); then fail "$label: command unexpectedly succeeded"; fi
}

create_release() {
  local release="$1" commit="$2" release_dir manager manager_checksum
  release_dir="$test_root/releases/$release"
  manager="$release_dir/mcpanel-$commit.sh"
  mkdir -p -- "$release_dir"
  cat > "$manager" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$0" > "$BOOTSTRAP_MANAGER_PATH_LOG"
printf '%s\n' "$*" > "$BOOTSTRAP_HANDOFF_LOG"
EOF
  chmod 0755 "$manager"
  manager_checksum="$(sha256sum --binary -- "$manager" | awk '{print $1}')"
  {
    printf 'schema=1\n'
    printf 'commit=%s\n' "$commit"
    printf 'script_sha256=%s\n' "$manager_checksum"
    printf 'linux_x64_sha256=%064d\n' 0
    printf 'linux_arm64_sha256=%064d\n' 0
  } > "$release_dir/release-manifest.txt"
}

test_verified_handoff_and_cleanup() {
  local release="main" commit="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  local handoff_log="$test_root/handoff.log" manager_path_log="$test_root/manager-path.log" manager_path
  create_release "$release" "$commit"
  MCPANEL_RELEASE_BASE_URL="file://$test_root/releases" \
    BOOTSTRAP_HANDOFF_LOG="$handoff_log" \
    BOOTSTRAP_MANAGER_PATH_LOG="$manager_path_log" \
    "$test_repo_root/install" --release "$release" --listen-address 127.0.0.1 --port 6060 >/dev/null

  [[ "$(< "$handoff_log")" == "setup --release $release --listen-address 127.0.0.1 --port 6060" ]] || \
    fail "bootstrap did not forward setup arguments"
  manager_path="$(< "$manager_path_log")"
  [[ "$manager_path" == /tmp/mcpanel-bootstrap.*/*/mcpanel-*.sh ]] || fail "bootstrap used an unexpected manager path"
  [[ ! -e "$manager_path" ]] || fail "bootstrap temporary manager was not removed"
}

test_manifest_and_checksum_failures() {
  local malformed_dir="$test_root/releases/malformed" checksum_release="bad-checksum"
  local checksum_commit="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
  mkdir -p -- "$malformed_dir"
  printf 'schema=2\n' > "$malformed_dir/release-manifest.txt"
  assert_fails "malformed bootstrap manifest" env \
    MCPANEL_RELEASE_BASE_URL="file://$test_root/releases" \
    "$test_repo_root/install" --release malformed

  create_release "$checksum_release" "$checksum_commit"
  sed -i 's/^script_sha256=.*/script_sha256=ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff/' \
    "$test_root/releases/$checksum_release/release-manifest.txt"
  assert_fails "bootstrap manager checksum" env \
    MCPANEL_RELEASE_BASE_URL="file://$test_root/releases" \
    "$test_repo_root/install" --release "$checksum_release"
}

test_invalid_release_rejected_before_download() {
  assert_fails "unsafe bootstrap release" env \
    MCPANEL_RELEASE_BASE_URL="file://$test_root/releases" \
    "$test_repo_root/install" --release feature/main
}

test_verified_handoff_and_cleanup
test_manifest_and_checksum_failures
test_invalid_release_rejected_before_download
printf 'MC Panel bootstrap tests passed.\n'

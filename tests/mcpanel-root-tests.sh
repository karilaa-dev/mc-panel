#!/usr/bin/env bash

set -Eeuo pipefail
[[ "$EUID" -eq 0 ]] || { printf 'Run this test as root.\n' >&2; exit 1; }

test_repo_root="$(realpath -e -- "$(dirname -- "${BASH_SOURCE[0]}")/..")"
test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
export MCPANEL_SOURCE_ONLY=1
export MCPANEL_RELEASE_BASE_URL="file://$test_root/releases"
export MCPANEL_ROOT_TEST_SOURCE="$test_repo_root/mcpanel.sh"
export MCPANEL_ROOT_TEST_LOG="$test_root/handoff.log"
# shellcheck disable=SC1091
source "$test_repo_root/mcpanel.sh"

fail() { printf 'FAIL: %s\n' "$*" >&2; exit 1; }

# Exercise a root-only host with no sudo command in PATH.
mkdir -p "$test_root/bin"
for test_command in awk basename bash cat chmod cp curl dirname find getent grep gzip id mkdir mktemp realpath rm sha256sum systemctl tar tr uname; do
  ln -s "$(command -v "$test_command")" "$test_root/bin/$test_command"
done
export PATH="$test_root/bin"
if command -v sudo >/dev/null; then fail "sudo leaked into the fixture PATH"; fi
require_sudo_access
validate_access_user root
# The assertion runs in the child shell.
# shellcheck disable=SC2016
sudo_system bash -c '[[ "$EUID" -eq 0 && "$1" == "argument with spaces" ]]' -- 'argument with spaces'
test_rc=0
sudo_system bash -c 'exit 37' || test_rc=$?
[[ "$test_rc" -eq 37 ]] || fail "direct root execution lost the exit status"

test_commit=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
test_rid="$(detect_rid)"
test_release="$test_root/releases/main"
test_artifact="$test_root/artifact"
mkdir -p "$test_release" "$test_artifact/wwwroot"
printf '#!/usr/bin/env bash\nexit 0\n' > "$test_artifact/McPanel.Api"
chmod 0755 "$test_artifact/McPanel.Api"
printf '<!doctype html>\n' > "$test_artifact/wwwroot/index.html"
printf 'schema=1\nrelease=main\ncommit=%s\nrid=%s\n' "$test_commit" "$test_rid" > "$test_artifact/.mcpanel-release"

# Load the real manager in each child. Intercept only the final system mutation
# and interactive prompts, so tests cannot install services or alter host data.
cat > "$test_release/mcpanel-$test_commit.sh" <<'EOF'
#!/usr/bin/env bash
export MCPANEL_SOURCE_ONLY=1
# shellcheck disable=SC1090
source "$MCPANEL_ROOT_TEST_SOURCE"
script_path="$(realpath -e -- "$0")"
# shellcheck disable=SC2317,SC2329
wizard_open_tty() { :; }
# shellcheck disable=SC2317,SC2329
wizard_close_tty() { :; }
# shellcheck disable=SC2317,SC2329
wizard_confirm() { return 0; }
action="$1"
shift
case "$action" in
  setup) command_setup "$@" ;;
  __apply-prepared) apply_prepared_system_command "$@" ;;
  __install|__update)
    require_root
    validate_access_user "${!#}"
    [[ "${!#}" == root ]] || die "root identity was not forwarded"
    printf '%s\n' "$action" "$@" > "$MCPANEL_ROOT_TEST_LOG"
    if [[ "${MCPANEL_ROOT_TEST_FAIL:-0}" == 1 ]]; then
      printf 'error: fixture system operation failed\n' >&2
      exit 42
    fi
    ;;
  *) die "unexpected test manager action: $action" ;;
esac
EOF
chmod 0755 "$test_release/mcpanel-$test_commit.sh"
tar -czf "$test_release/mcpanel-$test_commit-$test_rid.tar.gz" -C "$test_artifact" .
test_script_sha="$(sha256sum "$test_release/mcpanel-$test_commit.sh")"
test_archive_sha="$(sha256sum "$test_release/mcpanel-$test_commit-$test_rid.tar.gz")"
printf 'schema=1\ncommit=%s\nscript_sha256=%s\nlinux_x64_sha256=%s\nlinux_arm64_sha256=%s\n' \
  "$test_commit" "${test_script_sha%% *}" "${test_archive_sha%% *}" "${test_archive_sha%% *}" > "$test_release/release-manifest.txt"

bash -s -- --install-dir "$test_root/install" --config-dir "$test_root/config" \
  --data-dir "$test_root/data" --listen-address 0.0.0.0 --port 6050 < "$test_repo_root/install" >/dev/null
grep -Fxq __install "$MCPANEL_ROOT_TEST_LOG" || fail "root bootstrap did not reach installation"

# Use child shells so production EXIT traps have the same lifetime as the CLI.
(
  trap - EXIT
  command_update --install-dir "$test_root/install" --config-dir "$test_root/config" --data-dir "$test_root/data"
) >/dev/null
grep -Fxq __update "$MCPANEL_ROOT_TEST_LOG" || fail "root update did not reach the privileged handoff"

test_rc=0
# shellcheck disable=SC2016
MCPANEL_ROOT_TEST_FAIL=1 bash -c 'source "$MCPANEL_ROOT_TEST_SOURCE"; command_update "$@"' -- \
  --install-dir "$test_root/install" --config-dir "$test_root/config" --data-dir "$test_root/data" \
  >/dev/null 2>"$test_root/failure.log" || test_rc=$?
[[ "$test_rc" -eq 42 ]] || fail "root update did not preserve failure status: $test_rc"
[[ "$(cat "$test_root/failure.log")" == 'error: fixture system operation failed' ]] || fail "root update cleanup obscured the failure"
mapfile -t test_handoff < "$MCPANEL_ROOT_TEST_LOG"
test_work_root="${test_handoff[1]%/*/*}"
[[ "$test_work_root" == /tmp/mcpanel-release.* ]] || fail "unexpected release work directory"
[[ ! -d "$test_work_root" ]] || fail "failed root update left its download directory behind"

printf 'MC Panel root installer tests passed.\n'

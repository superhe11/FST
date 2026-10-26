#!/usr/bin/env bash
# Reproduce CI locally without reusing or deleting the development Steam profile.
set -Eeuo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
mode="${1:-}"
[[ $# -le 1 && ( -z "$mode" || "$mode" == --steam ) ]] || {
  echo 'Usage: tools/Test-Linux-Sandbox.sh [--steam]' >&2; exit 2;
}
run_dir="$(mktemp -d "${TMPDIR:-/tmp}/fst-sandbox-check-XXXXXXXX")"
run_id="${run_dir##*/}"
# Always generate disposable resource names. Never inherit the user's dev names.
export FST_SANDBOX_NAME="$run_id"
export FST_SANDBOX_VOLUME="$run_id-home"
export FST_SANDBOX_IMAGE="localhost/fst-sandbox-check:$run_id"
# Port zero tells the launcher to request an available loopback port.
export FST_SANDBOX_PORT=0
results="${FST_SANDBOX_RESULTS:-$ROOT/analysis/sandbox-ci}"
results="$results/$run_id"
mkdir -p "$results"
results="$(cd -- "$results" && pwd -P)"
box="$ROOT/tools/fst-sandbox.sh"
cleanup() {
  status=$?
  trap - EXIT
  # Keep diagnostics, not profiles, login tokens or VNC passwords.
  if podman container exists "$FST_SANDBOX_NAME"; then
    podman logs --tail 100 "$FST_SANDBOX_NAME" > "$results/desktop.log" 2>&1 || true
    if ((status != 0)); then
      podman cp "$FST_SANDBOX_NAME:/home/dev/.vnc/server.log" "$results/xserver.log" 2>/dev/null || true
      podman cp "$FST_SANDBOX_NAME:/home/dev/.vnc/steam.log" "$results/steam-launch.log" 2>/dev/null || true
      podman cp "$FST_SANDBOX_NAME:/home/dev/.local/share/Steam/logs/console-linux.txt" "$results/steam-console.log" 2>/dev/null || true
    fi
    podman rm -f "$FST_SANDBOX_NAME" >/dev/null || true
  fi
  podman volume rm "$FST_SANDBOX_VOLUME" >/dev/null 2>&1 || true
  podman image rm "$FST_SANDBOX_IMAGE" >/dev/null 2>&1 || true
  rm -rf -- "$run_dir"
  exit "$status"
}
trap cleanup EXIT
# Let an in-flight launcher finish before cleanup; otherwise a background
# container creation could register after cleanup has already checked for it.
trap 'exit 130' INT
trap 'exit 143' TERM
if [[ "$mode" == --steam ]]; then
  storage="$(podman info --format '{{.Store.GraphRoot}}')"
  available_kb="$(df -Pk "$storage" | awk 'NR == 2 { print $4 }')"
  if ((available_kb < 12 * 1024 * 1024)); then
    echo 'The fresh Steam smoke test needs at least 12 GiB free in Podman storage.' >&2
    exit 1
  fi
fi
"$box" create > "$run_dir/create.log" 2>&1 || {
  # The password is only printed after successful creation.
  cat "$run_dir/create.log" >&2; exit 1;
}
rm "$run_dir/create.log"
"$box" doctor | tee "$results/isolation.log"
"$box" build 2>&1 | tee "$results/build.log"
"$box" test 2>&1 | tee "$results/tests.log"
"$box" exec bash tools/Test-Linux-Package.sh 2>&1 | tee "$results/package.log"
"$box" exec timeout 45 release/fst-linux-x64/app/fst --preview 2>&1 | tee "$results/gui.log"
podman cp "$FST_SANDBOX_NAME:/home/dev/.local/share/fst/linux-main.png" "$results/fst-linux.png"
test -s "$results/fst-linux.png"
if [[ "$mode" == --steam ]]; then
  "$box" steam
  ready=false
  # First launch downloads the client. Do not log in or use account secrets.
  deadline=$((SECONDS + 900))
  while ((SECONDS < deadline)); do
    if "$box" check-cef > "$run_dir/cef.json" 2>/dev/null; then ready=true; break; fi
    sleep 5
  done
  [[ "$ready" == true ]] || { echo 'Steam CEF did not become ready within 15 minutes.' >&2; exit 1; }
  python3 - "$run_dir/cef.json" <<'PY'
import json
import sys
with open(sys.argv[1]) as source:
    version = json.load(source)
assert version['Browser'].startswith('Chrome/'), version
assert version['webSocketDebuggerUrl'].startswith('ws://127.0.0.1:8080/'), version
print('PASS: fresh Steam client exposes CEF inside the sandbox without login')
PY
  "$box" doctor | tee "$results/isolation-after-steam.log"
fi
# Confirm profile persistence and that private runtime directories recover cleanly.
# Expand HOME inside the container, not in the host test runner.
# shellcheck disable=SC2016
"$box" exec bash -c 'printf persistent > "$HOME/.fst-ci-persistence"'
"$box" stop
"$box" start > /dev/null
# shellcheck disable=SC2016
"$box" exec bash -c 'test "$(cat "$HOME/.fst-ci-persistence")" = persistent'
"$box" doctor | tee "$results/isolation-after-restart.log"
printf 'PASS: fresh sandbox build, Linux tests, package, GUI and restart; results: %s\n' "$results"

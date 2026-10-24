#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
NAME="${FST_SANDBOX_NAME:-fst-sandbox}"
IMAGE="${FST_SANDBOX_IMAGE:-localhost/fst-sandbox:1}"
VOLUME="${FST_SANDBOX_VOLUME:-fst-sandbox-home}"
PORT="${FST_SANDBOX_PORT:-6080}"
usage() {
  cat <<'HELP'
Usage: tools/fst-sandbox.sh COMMAND [ARGS]
  create      Build the image and start the desktop
  start       Start the existing environment
  desktop     Print desktop URL and its password
  build       Sync sources and build/package in the sandbox
  test        Sync sources and run Linux self-tests
  app         Open the built FST in the sandbox desktop
  steam       Open the sandbox Steam client
  check-cef   Check the sandbox Steam debugging endpoint
  shell      Open a shell inside the sandbox
  exec ARGS   Run a command inside the sandbox
  export DIR  Copy release artifacts to a new host directory
  doctor      Verify isolation and desktop health
  stop        Stop the sandbox
  remove      Remove container; keep image and Steam profile
  purge       Delete container, image and Steam profile (asks for DELETE)
HELP
}
command="${1:-help}"
case "$command" in help|-h|--help) usage; exit 0 ;; esac
for dependency in python3 pasta; do
  command -v "$dependency" >/dev/null || { echo "Missing dependency: $dependency" >&2; exit 1; }
done
# The networking options below need a recent pasta; never fall back to host networking.
pasta --help 2>&1 | python3 -c 'import sys; sys.exit("--map-guest-addr" not in sys.stdin.read())' || {
  echo 'pasta must support --map-guest-addr (update the passt package).' >&2; exit 1;
}
command -v podman >/dev/null || { echo 'Install rootless Podman first.' >&2; exit 1; }
podman version --format '{{.Client.Version}}' | python3 -c '
import sys
version = tuple(int(part) for part in sys.stdin.read().strip().split(".")[:2])
if version < (5, 7):
    sys.exit("Podman 5.7 or newer is required.")
'
[[ "$(podman info --format '{{.Host.Security.Rootless}}')" == true ]] || { echo 'Run as an ordinary user with rootless Podman.' >&2; exit 1; }
start() {
  podman container exists "$NAME" || { echo 'Run create first.' >&2; exit 1; }
  podman start "$NAME" >/dev/null
  for ((i=0; i<60; i++)); do
    # shellcheck disable=SC2016
    if podman exec "$NAME" bash -c '
      test -s "$XDG_RUNTIME_DIR/session.env" && xdpyinfo >/dev/null 2>&1 &&
      curl -fsS http://127.0.0.1:6080/vnc.html >/dev/null
    ' >/dev/null 2>&1; then return; fi
    sleep 1
  done
  podman logs --tail 40 "$NAME" >&2
  echo 'Desktop did not become ready.' >&2; exit 1
}
desktop() {
  local port
  port="$(podman port "$NAME" 6080/tcp)"
  printf 'Desktop: http://%s/vnc.html?autoconnect=1&resize=remote\nPassword: ' "$port"
  podman exec "$NAME" cat /home/dev/.vnc/password
}
case "$command" in
  create)
    if [[ ! "$PORT" =~ ^[0-9]{1,5}$ ]]; then
      echo 'Invalid FST_SANDBOX_PORT' >&2; exit 2
    fi
    port_number=$((10#$PORT))
    if ((port_number != 0 && (port_number < 1024 || port_number > 65535))); then
      echo 'Invalid FST_SANDBOX_PORT' >&2; exit 2
    fi
    publish_port="$port_number"
    [[ "$port_number" != 0 ]] || publish_port=""
    podman build -t "$IMAGE" -f "$ROOT/tools/sandbox/Containerfile" "$ROOT/tools/sandbox"
    if ! podman container exists "$NAME"; then
      podman volume create --ignore "$VOLUME" >/dev/null
      podman create --name "$NAME" --hostname fst-sandbox \
        --userns=keep-id:uid=1000,gid=1000 --user 1000:1000 \
        --cap-drop=ALL --security-opt=no-new-privileges \
        --pid=private --ipc=private --network=pasta:--map-host-loopback,none,--map-guest-addr,none --no-hosts \
        --read-only --shm-size=512m --pids-limit=2048 \
        --mount type=tmpfs,destination=/run/user/1000,tmpfs-mode=0700,U=true \
        --mount "type=bind,source=$ROOT,target=/source,ro,relabel=shared" \
        --volume "$VOLUME:/home/dev" \
        --publish "127.0.0.1:$publish_port:6080" "$IMAGE" >/dev/null
    fi
    start
    podman exec "$NAME" control.sh sync
    desktop
    ;;
  start) start; desktop ;;
  desktop) start; desktop ;;
  build|test|check-cef) start; podman exec "$NAME" control.sh "$command" ;;
  app|steam)
    start
    if [[ "$command" == app ]]; then
      podman exec "$NAME" test -x /home/dev/work/release/fst-linux-x64/app/fst || {
        echo 'Run build first.' >&2; exit 1;
      }
    fi
    podman exec -d "$NAME" bash -c 'exec control.sh "$1" > "$HOME/.vnc/$1.log" 2>&1' bash "$command"
    ;;
  shell)
    start
    podman exec -it "$NAME" control.sh shell
    ;;
  exec)
    shift; start; podman exec -i "$NAME" control.sh exec "$@"
    ;;
  export)
    [[ $# == 2 && ! -e "$2" ]] || { echo 'Provide a new output directory.' >&2; exit 2; }
    podman cp "$NAME:/home/dev/work/release" "$2"
    ;;
  doctor)
    start
    podman inspect "$NAME" | python3 "$ROOT/tools/sandbox/verify.py" "$ROOT"
    podman exec "$NAME" bash -ec '
      test "$(id -u)" = 1000
      test ! -e /run/host
      test ! -S /run/user/1000/bus
      test ! -S /var/run/docker.sock
      if touch /source/.fst-sandbox-write-check 2>/dev/null; then
        rm /source/.fst-sandbox-write-check
        echo "FAIL: source mount is writable" >&2; exit 1
      fi
      xdpyinfo >/dev/null
      curl -fsS http://127.0.0.1:6080/vnc.html >/dev/null
      echo "PASS: non-root user, read-only sources, private desktop, no host session sockets"
    '
    ;;
  stop) podman stop "$NAME" ;;
  remove) podman rm -f "$NAME" ;;
  purge)
    printf 'Delete sandbox, image and test Steam profile? Type DELETE: ' >&2
    read -r answer
    [[ "$answer" == DELETE ]] || exit 1
    podman rm -f --ignore "$NAME"
    podman volume rm "$VOLUME"
    podman image rm "$IMAGE"
    ;;
  *) usage >&2; exit 2 ;;
esac

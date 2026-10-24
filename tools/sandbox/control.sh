#!/usr/bin/env bash
set -Eeuo pipefail
if [[ -f "$XDG_RUNTIME_DIR/session.env" ]]; then
  while IFS= read -r line; do export "${line?}"; done < "$XDG_RUNTIME_DIR/session.env"
fi
sync_source() {
  # Never build in the host checkout or import its binaries/settings.
  rsync -rlt --delete --exclude=.git --exclude=bin --exclude=obj --exclude=release \
    --exclude=.env --exclude='.env.*' --exclude=analysis /source/ "$HOME/work/"
}
cd "$HOME/work"
case "${1:-shell}" in
  sync) sync_source ;;
  build) sync_source; bash tools/Build-Linux.sh ;;
  test)
    sync_source
    dotnet run --project src/Fst.Linux/Fst.Linux.csproj -- --self-test
    ;;
  app)
    [[ -x release/fst-linux-x64/app/fst ]] || { echo 'Run build first.' >&2; exit 1; }
    exec release/fst-linux-x64/app/fst --show
    ;;
  steam) exec /usr/bin/steam -cef-enable-debugging ;;
  check-cef) exec curl --fail --show-error --silent --max-time 5 http://127.0.0.1:8080/json/version ;;
  shell) exec bash ;;
  exec) shift; exec "$@" ;;
  *) echo 'Unknown sandbox command' >&2; exit 2 ;;
esac

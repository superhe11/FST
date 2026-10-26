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
    luma_tool=/usr/lib/lumalinux/tools/steamidra_lite.py
    [[ -r "$luma_tool" ]] || { echo 'Missing lumalinux CLI; recreate the sandbox image.' >&2; exit 1; }
    dotnet run --project tests/Fst.Tests/Fst.Tests.csproj -c Release
    # Keep fixture tests offline and independent of caller-provided overrides.
    env -u FST_TEST_LUMA_TOOL -u FST_TEST_ARCHIVE \
      dotnet run --project src/Fst.Linux/Fst.Linux.csproj -c Release -- --self-test
    env -u FST_TEST_ARCHIVE FST_TEST_LUMA_TOOL="$luma_tool" \
      dotnet run --project src/Fst.Linux/Fst.Linux.csproj -c Release --no-build -- --self-test
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

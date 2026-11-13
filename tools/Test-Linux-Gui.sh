#!/usr/bin/env bash
# Linux UI smoke test. Dependencies, screenshots and settings stay in a sandbox.
set -euo pipefail
workspace="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
mode="${1:---preview}"
[[ "$mode" == --preview || "$mode" == --preview-en || "$mode" == --preview-notification || "$mode" == --preview-batch ]] || exit 1
test_root="$(mktemp -d /tmp/fst-gui-check-XXXXXXXX)"
mkdir -p "$test_root/config" "$test_root/data" "$test_root/deps"
if ! ldconfig -p 2>/dev/null | grep 'libICE.so.6' >/dev/null; then
  # Download/extract only: do not install packages in the host distro.
  (cd "$test_root/deps"; apt-get download libice6 libsm6)
  for archive in "$test_root"/deps/*.deb; do dpkg-deb -x "$archive" "$test_root/deps/root"; done
fi
XDG_CONFIG_HOME="$test_root/config" XDG_DATA_HOME="$test_root/data" LD_LIBRARY_PATH="$test_root/deps/root/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}" timeout 35s "$workspace/release/fst-linux-x64/app/fst" "$mode"
output="$workspace/analysis/linux-main.png"
if [[ "$mode" == --preview-batch ]]; then
  cp "$test_root/data/fst/linux-batch-progress.png" "$workspace/analysis/"
  printf 'PASS Linux batch progress/cancel UI: %s/analysis/linux-batch-progress.png\n' "$workspace"
  exit 0
fi
if [[ "$mode" == --preview-notification ]]; then
  cp "$test_root/data/fst/linux-notification-"{top,bottom}.png "$workspace/analysis/"
  printf 'PASS Linux notification layout and scrolling; screenshots: %s/analysis/linux-notification-{top,bottom}.png\n' "$workspace"
  exit 0
fi
if [[ "$mode" == --preview-en ]]; then output="$workspace/analysis/linux-main-en.png"; fi
cp "$test_root/data/fst/linux-main.png" "$output"
printf 'PASS Linux GUI screenshot: %s\n' "$output"

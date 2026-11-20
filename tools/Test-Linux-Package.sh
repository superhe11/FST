#!/usr/bin/env bash
set -euo pipefail
workspace="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$workspace/Directory.Build.props")"
archive="$workspace/release/fast-steam-tools-bin-${version}-1-x86_64.pkg.tar.zst"
test_root="$(mktemp -d /tmp/fst-package-check-XXXXXXXX)"
tar --zstd -xf "$archive" -C "$test_root"
grep -q '^pkgname = fast-steam-tools-bin$' "$test_root/.PKGINFO"
grep -q '^arch = x86_64$' "$test_root/.PKGINFO"
grep -q '^Exec=fst --show$' "$test_root/usr/share/applications/fst.desktop"
grep -Fxq 'Icon=fst' "$test_root/usr/share/applications/fst.desktop"
grep -Fxq 'StartupWMClass=fst' "$test_root/usr/share/applications/fst.desktop"
test -s "$test_root/usr/share/icons/hicolor/scalable/apps/fst.svg"
test -x "$test_root/opt/fst/fst"
test -x "$test_root/usr/bin/fst"
test -f "$test_root/usr/share/licenses/fast-steam-tools-bin/Avalonia-MIT.txt"
cmp "$test_root/opt/fst/fst" "$workspace/release/fst-linux-x64/app/fst"
bash -n "$workspace/packaging/linux/install.sh" "$workspace/packaging/linux/uninstall.sh" "$workspace/release/PKGBUILD"
"$test_root/opt/fst/fst" --self-test
(cd "$workspace/release"; sha256sum -c SHA256SUMS-linux.txt)
printf 'PASS Arch package payload, permissions, metadata, launcher and checksums\n'

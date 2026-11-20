#!/usr/bin/env bash
# Run in a fresh, disposable Fedora container as root.
set -euo pipefail
workspace="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$workspace/Directory.Build.props")"
package=fast-steam-tools
rpm_file="$workspace/release/$package-$version-1.x86_64.rpm"
binary="/usr/libexec/$package/fst"
desktop=/usr/share/applications/fst.desktop
icon=/usr/share/icons/hicolor/scalable/apps/fst.svg
licenses="/usr/share/licenses/$package"

if rpm -q "$package" >/dev/null; then
  echo 'Run this test on a fresh Fedora installation.' >&2
  exit 1
fi
test "$(rpm -qp --qf '%{NAME}-%{VERSION}-%{RELEASE}.%{ARCH}' "$rpm_file")" = "$package-$version-1.x86_64"
dnf -y --setopt=install_weak_deps=False install "$rpm_file"
rpm -V "$package"
command -v python3 secret-tool xdg-open

dnf -y --setopt=install_weak_deps=False install desktop-file-utils diffutils util-linux-core xorg-x11-server-Xvfb xorg-x11-xauth
test "$(readlink -f /usr/bin/fst)" = "$binary"
cmp "$binary" "$workspace/release/fst-linux-x64/app/fst"
desktop-file-validate "$desktop"
grep -Fx 'Exec=fst --show' "$desktop"
grep -Fx 'Icon=fst' "$desktop"
grep -Fx 'StartupWMClass=fst' "$desktop"
test -s "$icon"
for license in "$workspace"/packaging/linux/LICENSES/*.txt; do
  cmp "$license" "$licenses/${license##*/}"
done
test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
chown nobody -- "$test_root"
as_user=(runuser -u nobody -- env HOME="$test_root" XDG_CONFIG_HOME="$test_root/config" XDG_DATA_HOME="$test_root/data")
"${as_user[@]}" /usr/bin/fst --self-test
"${as_user[@]}" timeout 45s xvfb-run -a /usr/bin/fst --preview-en
test -s "$test_root/data/fst/linux-main.png"

dnf -y remove "$package"
if rpm -q "$package" >/dev/null; then
  echo 'FST remains registered after removal.' >&2
  exit 1
fi
for path in /usr/bin/fst "$binary" "$desktop" "$icon" "$licenses"; do
  test ! -e "$path" && test ! -L "$path" || { printf 'File remains after removal: %s\n' "$path" >&2; exit 1; }
done
test -s "$test_root/config/fst/settings.json"
printf 'PASS Fedora RPM metadata, dependencies, launcher, desktop, offline self-test and removal\n'

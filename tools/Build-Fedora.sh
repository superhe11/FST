#!/usr/bin/env bash
set -euo pipefail
workspace="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' "$workspace/Directory.Build.props")"
archive="$workspace/release/fst-$version-linux-x86_64.tar.gz"
[[ -s "$archive" ]] || { printf 'Build the Linux release archive first: %s\n' "$archive" >&2; exit 1; }

build_root="$(mktemp -d)"
trap 'rm -rf -- "$build_root"' EXIT
rpmbuild -bb --target x86_64 \
  --define "_topdir $build_root" \
  --define "_sourcedir $workspace/release" \
  --define "fst_version $version" \
  "$workspace/packaging/fedora/fast-steam-tools.spec"
rpm="$build_root/RPMS/x86_64/fast-steam-tools-$version-1.x86_64.rpm"
install -m 644 -- "$rpm" "$workspace/release/"
printf 'Fedora package: %s/release/%s\n' "$workspace" "${rpm##*/}"

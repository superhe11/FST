#!/usr/bin/env bash
# Validate the artifacts from this workflow before making them public.
set -euo pipefail
tag="${1:?Provide the release tag}"
assets="${2:?Provide the artifact directory}"
[[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || exit 2
version="${tag#v}"
test -s "$assets/FST-Setup.exe"
test -s "$assets/fst-$version-linux-x86_64.tar.gz"
test -s "$assets/fast-steam-tools-bin-$version-1-x86_64.pkg.tar.zst"
test -s "$assets/PKGBUILD"
test -s "$assets/SHA256SUMS-linux.txt"
grep -Fx "pkgver=$version" "$assets/PKGBUILD"
(cd "$assets"; sha256sum --check SHA256SUMS-linux.txt)
if [[ "$(find "$assets" -mindepth 1 -maxdepth 1 -type f | wc -l)" != 5 ]]; then
  echo 'Unexpected release artifacts.' >&2; exit 1
fi
{
  for license in packaging/linux/LICENSES/*.txt; do
    printf '\n===== %s =====\n\n' "${license##*/}"
    cat "$license"
  done
} > "$assets/THIRD-PARTY-NOTICES.txt"
(cd "$assets"; sha256sum FST-Setup.exe PKGBUILD SHA256SUMS-linux.txt THIRD-PARTY-NOTICES.txt \
  "fst-$version-linux-x86_64.tar.gz" "fast-steam-tools-bin-$version-1-x86_64.pkg.tar.zst" > SHA256SUMS.txt
  sha256sum --check SHA256SUMS.txt)

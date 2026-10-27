#!/usr/bin/env bash
set -euo pipefail
workspace="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$workspace"
if [[ "${1:-}" != --package-only ]]; then
  dotnet publish src/Fst.Linux/Fst.Linux.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o release/fst-linux-x64/app --nologo
fi
cp packaging/linux/install.sh packaging/linux/uninstall.sh packaging/linux/fst.svg packaging/linux/fst.desktop packaging/linux/fst-launcher packaging/linux/README-LINUX.md release/fst-linux-x64/
mkdir -p release/fst-linux-x64/LICENSES
cp packaging/linux/LICENSES/* release/fst-linux-x64/LICENSES/
chmod 755 release/fst-linux-x64/app/fst release/fst-linux-x64/install.sh release/fst-linux-x64/uninstall.sh release/fst-linux-x64/fst-launcher
version="$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' Directory.Build.props)"
archive_name="fst-${version}-linux-x86_64.tar.gz"
tar --exclude='*.pdb' -czf "release/$archive_name" -C release fst-linux-x64
archive_hash="$(sha256sum "release/$archive_name" | cut -d ' ' -f 1)"
sed "s/REPLACE_WITH_RELEASE_SHA256/$archive_hash/" packaging/arch/PKGBUILD > release/PKGBUILD
# Arch package assembled from the same PKGBUILD package() function.
build_root="$(mktemp -d /tmp/fst-package-XXXXXXXX)"
srcdir="$build_root/source"
pkgdir="$build_root/package"
mkdir -p "$srcdir" "$pkgdir"
tar -xzf "release/$archive_name" -C "$srcdir"
source release/PKGBUILD
package
size="$(du -sb "$pkgdir" | cut -f 1)"
{
  printf 'pkgname = %s\npkgbase = %s\npkgver = %s-%s\npkgdesc = %s\narch = x86_64\n' "$pkgname" "$pkgname" "$pkgver" "$pkgrel" "$pkgdesc"
  printf 'builddate = %s\nsize = %s\npackager = Fast Steam Tools local build\nlicense = custom\n' "$(date +%s)" "$size"
  for dependency in "${depends[@]}"; do printf 'depend = %s\n' "$dependency"; done
  for dependency in "${optdepends[@]}"; do printf 'optdepend = %s\n' "$dependency"; done
  printf 'provides = fst\nconflict = fast-steam-tools\n'
} > "$pkgdir/.PKGINFO"
tar --zstd --owner=0 --group=0 -cf "release/${pkgname}-${pkgver}-${pkgrel}-x86_64.pkg.tar.zst" -C "$pkgdir" .PKGINFO opt usr
(cd release; sha256sum "$archive_name" "${pkgname}-${pkgver}-${pkgrel}-x86_64.pkg.tar.zst" > SHA256SUMS-linux.txt)
printf 'Linux artifacts: %s/release\n' "$workspace"

#!/usr/bin/env bash
set -euo pipefail
package_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
data_root="${XDG_DATA_HOME:-$HOME/.local/share}/fst"
config_root="${XDG_CONFIG_HOME:-$HOME/.config}"
icon_theme="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor"
local_bin="$HOME/.local/bin"
startup=yes
launch=yes
for argument in "$@"; do
  case "$argument" in
    --no-startup) startup=no ;;
    --no-launch) launch=no ;;
    *) printf 'Unknown option: %s\n' "$argument" >&2; exit 1 ;;
  esac
done
[[ "$(uname -m)" == x86_64 ]] || { echo 'FST requires Linux x86_64.' >&2; exit 1; }
command -v python3 >/dev/null || { echo 'python3 is required.' >&2; exit 1; }
[[ -f "$package_dir/app/fst" ]] || { echo 'Application payload missing.' >&2; exit 1; }
if pgrep -u "$(id -u)" -x fst >/dev/null; then echo 'Exit FST before installing an update.' >&2; exit 1; fi
if [[ -t 0 && $# -eq 0 ]]; then
  read -r -p 'Start FST with your desktop session? [Y/n] ' answer
  [[ "${answer,,}" != n ]] || startup=no
fi
if [[ -e "$local_bin/fst" ]] && ! grep -q '^# FastSteamTools launcher$' "$local_bin/fst"; then
  echo 'A different ~/.local/bin/fst already exists; preserving it.' >&2; exit 1
fi
[[ ! -L "$local_bin/fst" ]] || { echo 'Refusing to replace a launcher symlink.' >&2; exit 1; }
mkdir -p -- "$data_root/app" "$local_bin" "$config_root/autostart" "${XDG_DATA_HOME:-$HOME/.local/share}/applications" "$icon_theme/scalable/apps"
install -m 755 -- "$package_dir/app/fst" "$data_root/app/fst.next"
mv -f -- "$data_root/app/fst.next" "$data_root/app/fst"
install -m 644 -- "$package_dir/fst.svg" "$icon_theme/scalable/apps/fst.svg"
# Notify desktops that cache icons using the theme directory's modification time.
touch -m -- "$icon_theme"
python3 - "$data_root/app/fst" "$local_bin/fst" "${XDG_DATA_HOME:-$HOME/.local/share}/applications/fst.desktop" "$config_root/autostart/fst.desktop" "$startup" <<'PY'
import os, shlex, sys
executable, launcher, entry, autostart, startup = sys.argv[1:]
with open(launcher, 'w', encoding='utf-8') as f:
    f.write('#!/bin/sh\n# FastSteamTools launcher\nexec '+shlex.quote(executable)+' "$@"\n')
os.chmod(launcher, 0o755)
def desktop(arguments):
    value=executable.replace('\\','\\\\').replace('"','\\"').replace('`','\\`').replace('$','\\$').replace('%','%%')
    return '[Desktop Entry]\nType=Application\nName=Fast Steam Tools\nExec="'+value+'" '+arguments+'\nIcon=fst\nStartupWMClass=fst\nTerminal=false\nCategories=Game;Utility;\n'
with open(entry, 'w', encoding='utf-8') as f: f.write(desktop('--show'))
if startup == 'yes':
    with open(autostart, 'w', encoding='utf-8') as f: f.write(desktop('--background'))
elif os.path.isfile(autostart): os.unlink(autostart)
PY
printf 'Installed: %s\n' "$data_root/app/fst"
if ! command -v secret-tool >/dev/null; then
  echo 'Install libsecret and enable a Secret Service keyring (GNOME Keyring or compatible KDE wallet) for saved Ryuu sign-in.'
fi
echo 'The Linux backend requires SLSsteam + lumalinux. See README-LINUX.md.'
if [[ "$launch" == yes ]]; then
  if [[ -t 0 ]]; then read -r -p 'Press Enter to finish and open FST. ' _; fi
  "$data_root/app/fst" --show >/dev/null 2>&1 &
fi

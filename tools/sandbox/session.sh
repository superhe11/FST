#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
mkdir -p "$HOME/.vnc" "$HOME/.local/share" "$HOME/.config" "$HOME/work"
mkdir -p "$HOME/.config/openbox"
cat > "$HOME/.config/openbox/menu.xml" <<'MENU'
<?xml version="1.0" encoding="UTF-8"?>
<openbox_menu xmlns="http://openbox.org/3.4/menu">
  <menu id="root-menu" label="FST sandbox">
    <item label="Fast Steam Tools"><action name="Execute"><command>control.sh app</command></action></item>
    <item label="Steam (test profile)"><action name="Execute"><command>control.sh steam</command></action></item>
    <item label="Firefox"><action name="Execute"><command>firefox</command></action></item>
    <item label="Terminal"><action name="Execute"><command>xterm</command></action></item>
  </menu>
</openbox_menu>
MENU
if [[ ! -s "$HOME/.vnc/password" ]]; then
  # VNC authentication uses eight characters. Keep the secret inside the volume.
  python -c 'import secrets; print(secrets.token_hex(4))' > "$HOME/.vnc/password"
  vncpasswd -f < "$HOME/.vnc/password" > "$HOME/.vnc/passwd"
fi
Xvnc :0 -geometry 1440x900 -depth 24 -localhost yes -AlwaysShared \
  -SecurityTypes VncAuth -PasswordFile "$HOME/.vnc/passwd" -nolisten tcp > "$HOME/.vnc/server.log" 2>&1 &
vnc_pid=$!
trap 'kill "$vnc_pid" "${web_pid:-}" "${desktop_pid:-}" 2>/dev/null || true; wait || true' EXIT
for ((i=0; i<100; i++)); do
  if xdpyinfo >/dev/null 2>&1; then break; fi
  kill -0 "$vnc_pid" || { cat "$HOME/.vnc/server.log"; exit 1; }
  sleep .1
done
xdpyinfo >/dev/null
# A private D-Bus and keyring; neither uses the host login session.
# Variables in this command belong to the private D-Bus session.
# shellcheck disable=SC2016
dbus-run-session -- bash -c '
  eval "$(gnome-keyring-daemon --start --components=secrets)"
  env | grep -E "^(DBUS_SESSION_BUS_ADDRESS|GNOME_KEYRING_CONTROL)=" > "$XDG_RUNTIME_DIR/session.env"
  openbox &
  xterm -title "FST sandbox" -e bash -lc "cd \"\$HOME/work\"; exec bash" &
  wait
' > "$HOME/.vnc/desktop.log" 2>&1 &
desktop_pid=$!
/opt/websockify/bin/websockify --web /opt/novnc 6080 localhost:5900 &
web_pid=$!
wait -n "$vnc_pid" "$desktop_pid" "$web_pid"

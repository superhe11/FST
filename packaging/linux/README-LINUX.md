# Fast Steam Tools — Arch Linux x86_64

Native desktop build, .NET runtime included. No Wine required. Steam must be the native pacman/AUR installation; this build does not target Flatpak or Windows Steam.

## Install

Install desktop dependencies on Arch:

```sh
sudo pacman -S --needed python libx11 libice libsm fontconfig libsecret xdg-utils ttf-dejavu icu openssl zlib
```

A Secret Service provider must be running and unlocked to save Ryuu sign-in. For example, GNOME Keyring or a desktop wallet that implements Secret Service. FST never falls back to storing the key in a text file.

Extract `fst-0.1.3-linux-x86_64.tar.gz`, open a terminal in `fst-linux-x64` and run:

```sh
bash install.sh
```

The installer asks about startup, then opens FST after the final Enter. Install location: `${XDG_DATA_HOME:-~/.local/share}/fst/app`. The desktop menu entry is Fast Steam Tools. Closing the main window hides it in the tray; Exit terminates the app. GNOME may need a StatusNotifier/AppIndicator extension for tray icons. The desktop shortcut still opens the window if a tray icon is unavailable.

Alternatively put `PKGBUILD` next to the archive and run `makepkg -si`. This installs a system-managed package; remove it with `sudo pacman -R fast-steam-tools-bin`. It does not enable startup automatically. This PKGBUILD has not been submitted to AUR.

## Steam integration

The native download backend requires **SLSsteam + lumalinux**. Install that stack using its upstream desktop instructions:
https://github.com/jayool/lumalinux#manual-desktop-no-plugin

For a standalone SLSsteam Arch package, its instructions are at https://github.com/AceSLS/SLSsteam/wiki/Installation. SLSsteam alone registers games but does not supply the depot keys needed for native downloading. FST does not bundle or execute third-party component installers.

FST detects `~/.steam/steam` or `~/.local/share/Steam`, resolves the standard Steam directory symlink, and looks for SLSsteam.so and liblumalinux.so in common user/system locations. Use Folders to select the Steam directory and both libraries if needed. On restart it prefers the installed upstream Steam wrapper; without one it uses `LD_AUDIT` for SLSsteam and `LD_PRELOAD` for lumalinux, preserving pre-existing entries. Empty/missing `library-inject.so` is skipped (newer SLSsteam releases ship an empty placeholder).

The Ryuu flow restores the saved system-keyring connection, then tries Ryuu's server-side session discovery, then opens Discord sign-in when needed. Logout from a browser does not necessarily remove the server session returned by Ryuu.

The Linux adapter validates the archive and declarative Lua, then delegates game deployment to lumalinux's installed `tools/steamidra_lite.py` via Python 3. The tool handles Lua, manifests, depot keys, SLSsteam registration/pins, Steam `config.vdf` keys and existing `.acf` error recovery, including secondary libraries. FST runs it against isolated copies and commits the results with recovery backups only after success. Lua is parsed as data and never executed.

After download, FST requests a graceful Steam shutdown and waits for the client and web helpers to exit before preparing any copies. It refuses installation if Steam is running again before commit. The “Start Steam after adding a game” setting controls whether Steam is launched again after success; with it disabled, Steam stays closed. A failed installation leaves Steam closed for recovery. Manual restart in FST shares the installation lock.

The tool is detected next to `liblumalinux.so`, normally at `~/.local/share/lumalinux/tools/steamidra_lite.py`. A custom path can be selected in Folders. If it is missing, update lumalinux using its upstream installer. FST uses the installed copy so it stays paired with the installed library; it does not download or bundle the tool. Auto-update uses the tool's default mode; disabling auto-update passes `--pin`. Explicit manual keys and pins take precedence. Manual depot keys are applied to the input Lua and existing staged `config.vdf` entries before the CLI runs, so VDF injection uses the same key as `keys.txt`. Only the requested game's Lua is deployed from a multi-Lua archive.

While FST-managed games are present, the adapter sets SLSsteam `DisableUpdates` to false so that SLSsteam does not block their native download/update path. The prior setting is restored when the last FST game is removed, unless it was manually changed. Removing scripts restores FST-owned SLS/key registrations where they have not subsequently been changed by the user, and keeps recovery backups. Legacy FST key blocks and ownership metadata are imported automatically. Cached manifests and Steam’s `config.vdf` depot keys remain available to Steam after removal.

If a commit fails, rollback restores every successfully modified file and continues past individual restoration failures. An incomplete rollback reports the backup directory, retains the original error and restoration errors, and saves `rollback-errors.json` next to `restore.json`.

Install both SLSsteam and lumalinux before adding a game through FST.

Steam's local CEF interface (port 8080) is used for the store button. Restart Steam once through FST to create the debugging marker and enable it. The button and RU/EN messages reuse the Windows store frontend. No local HTTP installation server is started.

## Data and removal

- Settings: `${XDG_CONFIG_HOME:-~/.config}/fst/settings.json`.
- Logs/backups/SLS ownership metadata: `${XDG_DATA_HOME:-~/.local/share}/fst`.
- Keyring attributes: `application=fst`, `service=ryuu`.
- Native depot keys: `~/.config/lumalinux/keys.txt` (the upstream loader uses this fixed HOME-relative default; unlike FST's settings, it does not follow XDG_CONFIG_HOME).
- Startup entry: `${XDG_CONFIG_HOME:-~/.config}/autostart/fst.desktop`.

To uninstall the per-user build, exit FST and run `bash uninstall.sh`. It preserves Steam, Lua scripts and recovery backups. The in-app Uninstall screen additionally offers removing the Lua scripts. The package build should be removed using pacman; the app cannot remove root-owned package files.

## Validation and build

```sh
./app/fst --self-test
dotnet publish src/Fst.Linux/Fst.Linux.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

`--self-test` uses an isolated temporary directory and an offline CLI fixture, and makes no Ryuu/Steam network requests. To also exercise an installed upstream tool against temporary Steam data, run `FST_TEST_LUMA_TOOL=/path/to/steamidra_lite.py ./app/fst --self-test`.

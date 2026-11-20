# Fast Steam Tools for Arch Linux and Fedora (x86_64)

Native desktop build with the .NET runtime included. Use a native Steam installation from pacman/AUR on Arch or RPM Fusion on Fedora. Flatpak and Windows Steam are unsupported.

## Install the Fedora RPM

Download the x86_64 RPM from [Releases](https://github.com/superhe11/FST/releases). Install the RPM and launch FST from the download folder.

```sh
sudo dnf install ./fast-steam-tools-*.x86_64.rpm
fst
```

DNF installs FST's desktop dependencies. On a Wayland desktop, FST uses XWayland. Saving Ryuu sign-in requires a running, unlocked Secret Service provider, such as GNOME Keyring or a compatible desktop wallet. Follow the Steam integration steps below before using FST to add games.

## Install from the archive or PKGBUILD

For a per-user archive install on Arch, install the desktop dependencies with pacman.

```sh
sudo pacman -S --needed python libx11 libice libsm fontconfig libsecret xdg-utils ttf-dejavu icu openssl zlib
```

On Fedora, install the desktop dependencies with DNF.

```sh
sudo dnf install ca-certificates glibc libgcc libstdc++ libicu openssl-libs krb5-libs tzdata zlib python3 libX11 libICE libSM fontconfig libsecret xdg-utils dejavu-sans-fonts xorg-x11-server-Xwayland
```

A Secret Service provider must be running and unlocked to save Ryuu sign-in. For example, GNOME Keyring or a desktop wallet that implements Secret Service. FST never falls back to storing the key in a text file.

Extract `fst-0.1.5-linux-x86_64.tar.gz`, open a terminal in `fst-linux-x64` and run:

```sh
bash install.sh
```

The installer asks about startup, then opens FST after the final Enter. Install location: `${XDG_DATA_HOME:-~/.local/share}/fst/app`. The desktop menu entry is Fast Steam Tools. Closing the main window hides it in the tray; Exit terminates the app. GNOME may need a StatusNotifier/AppIndicator extension for tray icons. The desktop shortcut still opens the window if a tray icon is unavailable.

Alternatively put `PKGBUILD` next to the archive and run `makepkg -si`. This installs a system-managed package; remove it with `sudo pacman -R fast-steam-tools-bin`. It does not enable startup automatically. This PKGBUILD has not been submitted to AUR.

## Steam integration

The native download backend requires **SLSsteam + lumalinux**. Install that stack using its upstream desktop instructions:
https://github.com/jayool/lumalinux#manual-desktop-no-plugin

For a standalone SLSsteam Arch package, its instructions are at https://github.com/AceSLS/SLSsteam/wiki/Installation. SLSsteam alone registers games but does not supply the depot keys needed for native downloading. FST does not bundle or execute third-party component installers.

FST detects `~/.steam/steam` or `~/.local/share/Steam`, resolves the standard Steam directory symlink, and looks for SLSsteam.so and liblumalinux.so in common user/system locations. Use Folders to select the Steam directory and both libraries if needed. By default, FST uses the installed Steam wrapper and preserves its environment. Without a wrapper it launches Steam through PATH and adds the selected libraries to the inherited loader settings. If the wrapper fails, disable **Use installed Steam wrapper** in Folders. This selects the native system launcher and loads the selected SLSsteam and lumalinux libraries directly. This preference applies to both manual and automatic restarts.

The Ryuu flow restores the saved system-keyring connection, then tries Ryuu's server-side session discovery, then opens Discord sign-in when needed. Logout from a browser does not necessarily remove the server session returned by Ryuu.

The Linux adapter validates the archive and declarative Lua, then delegates game deployment to lumalinux's installed `tools/steamidra_lite.py` via Python 3. The tool handles Lua, manifests, depot keys, SLSsteam registration/pins, Steam `config.vdf` keys and existing `.acf` error recovery, including secondary libraries. FST runs it against isolated copies and commits the results with recovery backups only after success. Lua is parsed as data and never executed.

After download, FST requests a graceful Steam shutdown and waits for the client and web helpers to exit before preparing any copies. It refuses installation if Steam is running again before commit. Enable **Start Steam after adding a game** to reopen the native Steam client after a successful installation. The manual **Restart Steam** button uses the same launcher and shares the installation lock. With the wrapper disabled, FST uses the system Steam launcher, or `steam.sh` from the configured Steam directory if the system launcher is unavailable. A failed installation leaves Steam closed for recovery.

The tool is detected next to `liblumalinux.so`, normally at `~/.local/share/lumalinux/tools/steamidra_lite.py`. A custom path can be selected in Folders. If it is missing, update lumalinux using its upstream installer. FST uses the installed copy so it stays paired with the installed library; it does not download or bundle the tool. Auto-update uses the tool's default mode; disabling auto-update passes `--pin`. Explicit manual keys and pins take precedence. Manual depot keys are applied to the input Lua and existing staged `config.vdf` entries before the CLI runs, so VDF injection uses the same key as `keys.txt`. Only the requested game's Lua is deployed from a multi-Lua archive.

While FST-managed games are present, the adapter sets SLSsteam `DisableUpdates` to false so that SLSsteam does not block their native download/update path. The prior setting is restored when the last FST game is removed, unless it was manually changed. Removing scripts restores FST-owned SLS/key registrations where they have not subsequently been changed by the user, and keeps recovery backups. Legacy FST key blocks and ownership metadata are imported automatically. Cached manifests and Steam’s `config.vdf` depot keys remain available to Steam after removal.

If a commit fails, rollback restores every successfully modified file and continues past individual restoration failures. An incomplete rollback reports the backup directory, retains the original error and restoration errors, and saves `rollback-errors.json` next to `restore.json`.

Install both SLSsteam and lumalinux before adding a game through FST.

FST uses Steam's local CEF interface (port 8080) for its store UI when the endpoint is available. The button and RU/EN messages reuse the Windows store frontend. No local HTTP installation server is started.

## Data and removal

- Settings: `${XDG_CONFIG_HOME:-~/.config}/fst/settings.json`.
- Logs/backups/SLS ownership metadata: `${XDG_DATA_HOME:-~/.local/share}/fst`.
- Keyring attributes: `application=fst`, `service=ryuu`.
- Native depot keys: `~/.config/lumalinux/keys.txt` (the upstream loader uses this fixed HOME-relative default; unlike FST's settings, it does not follow XDG_CONFIG_HOME).
- Startup entry: `${XDG_CONFIG_HOME:-~/.config}/autostart/fst.desktop`.

To uninstall the per-user build, exit FST and run `bash uninstall.sh`. It preserves Steam, Lua scripts and recovery backups. The in-app Uninstall screen additionally offers removing the Lua scripts. Remove the system package with `sudo dnf remove fast-steam-tools` on Fedora or `sudo pacman -R fast-steam-tools-bin` on Arch.

## Validation and build

```sh
./app/fst --self-test
dotnet publish src/Fst.Linux/Fst.Linux.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

`--self-test` uses an isolated temporary directory and an offline CLI fixture, and makes no Ryuu/Steam network requests. To also exercise an installed upstream tool against temporary Steam data, run `FST_TEST_LUMA_TOOL=/path/to/steamidra_lite.py ./app/fst --self-test`.

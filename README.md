# Fast Steam Tools

<img src="packaging/linux/fst.svg" alt="FST" width="72">

FST is a utility for adding games to Steam for free using Lua manifests.

[Download](https://github.com/superhe11/FST/releases) · [Report an issue](https://github.com/superhe11/FST/issues) · [Русский](README.ru.md) · [Linux sandbox](LINUX_DEV.md)

## Components and credits

FST integrates the following projects and services:

- [Ryuu](https://generator.ryuu.lol/) — Lua and manifest downloads.
- [BetterSteamTools](https://github.com/madoiscool/BetterSteamTools) — Lua loading on Windows.
- [SLSsteam](https://github.com/AceSLS/SLSsteam) — Steam library integration on Linux.
- [lumalinux](https://github.com/jayool/lumalinux) — game deployment and native Steam downloads on Linux, through its installed `steamidra_lite.py` tool (Python 3).

## Installation

### Windows

Requires Windows 10/11 x64 and Steam.

1. Download **FST-Setup.exe** from [Releases](https://github.com/superhe11/FST/releases).
2. Run the installer, choose whether to start FST with Windows, and click **Install**.
3. Wait for setup to finish and Steam to restart, then click **Done** to open FST.

Administrator privileges are not required. If Steam is not detected, select its folder in **Folders**.

### Arch Linux

For native Steam installed through pacman/AUR on x86_64. Flatpak Steam is not supported.

1. Install **SLSsteam and lumalinux** using the [setup guide](https://github.com/jayool/lumalinux#manual-desktop-no-plugin).
2. Download **fast-steam-tools-bin-0.1.4-1-x86_64.pkg.tar.zst** from [Releases](https://github.com/superhe11/FST/releases). Open a terminal in the download folder and run:

   ```bash
   sudo pacman -U ./fast-steam-tools-bin-0.1.4-1-x86_64.pkg.tar.zst
   fst
   ```

3. Restart Steam using the button in FST. If Steam or the loaders are not detected, select them in **Folders**.

Saving your sign-in requires a running, unlocked password keyring, such as GNOME Keyring.

For installation from the archive and additional settings, see the [Linux guide](packaging/linux/README-LINUX.md).

## Usage

1. Click **Connect** in FST and sign in through Discord. Your connection is saved for future launches.
2. Open the game's store page inside Steam.
3. Click **Add to library for free** and wait for completion. FST installs the Lua and manifests, then restarts Steam if that setting is enabled.

FST runs in the system tray. Click its icon to open the menu. Use **RU / en** to switch the interface and log language.

In **Added Lua**, search by game name or AppID and remove one or more entries.

## Troubleshooting

- **No button on the game page:** restart Steam through FST.
- **Steam not detected:** select its folder in **Folders**.
- **Installation or connection error:** open **Logs**. If setup has not finished yet, press Win+R on Windows and enter `%LOCALAPPDATA%\FST\fst.log`.

If the problem persists, [open an issue](https://github.com/superhe11/FST/issues) with the error message, FST version and operating system.

## Uninstall

On Windows, click **Uninstall FST** in the app. You can also choose to remove added Lua.

On Arch Linux:

```bash
sudo pacman -R fast-steam-tools-bin
```

## Build from source

Requires .NET 9 SDK. From the repository root:

```powershell
# Windows
powershell -NoProfile -File tools/Build-Setup.ps1
```

```bash
# Linux
bash tools/Build-Linux.sh
```

Build outputs are saved in `release/`.

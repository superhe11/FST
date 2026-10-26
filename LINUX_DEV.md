# Linux development sandbox

FST uses **rootless Podman with a private desktop**, accessible in a browser through noVNC. Steam, FST, Firefox, the password keyring and .NET 9 run inside the container. No development or Steam packages are installed on the host.

## Start

Requirements on the host: Linux x86_64, rootless Podman 5.7 or newer, pasta networking (the `passt` package with `--map-guest-addr` support), Bash, Python 3, and a browser. Reserve roughly 10–12 GB for the image, rootless filesystem copies, build cache and initial Steam installation, plus downloaded games. Internet access is required for setup and package restore.

From this checkout:

```bash
./tools/fst-sandbox.sh create
./tools/fst-sandbox.sh build
./tools/fst-sandbox.sh app
./tools/fst-sandbox.sh steam
```

`create` prints the desktop URL and a generated VNC password. Open that URL, choose Connect, and enter the password. Retrieve these again with `./tools/fst-sandbox.sh desktop`. The default URL is <http://127.0.0.1:6080/vnc.html?autoconnect=1&resize=remote>. Set `FST_SANDBOX_PORT=6081` **when creating** if 6080 is occupied.

Steam's first launch downloads its client. Sign in using a dedicated test account. Use Firefox **inside the sandbox** for browser login flows; callback localhost belongs to the container. Right-click the desktop for Openbox's menu; a terminal opens at startup. There is no host keyring integration; initialize/unlock the sandbox keyring if you want FST to remember sign-in.

FST detects the image's SLSsteam and lumalinux libraries. The image also installs `steamidra_lite.py` under `/usr/lib/lumalinux/tools/`, from the same release tag as `liblumalinux.so`; FST discovers it next to that library. Recreate older sandbox images that only contain the shared library before testing game deployment. The `steam` command enables CEF debugging; use FST's Steam restart button to launch Steam with the loaders. `check-cef` is expected to fail until the Steam client has finished its initial updates and started its web helper. The debugging port 8080 is never published to the host.

## Everyday commands

```bash
./tools/fst-sandbox.sh build          # sync current sources, compile and package
./tools/fst-sandbox.sh test           # core tests, Linux fixture tests and installed lumalinux CLI tests
./tools/fst-sandbox.sh app            # show built FST in private desktop
./tools/fst-sandbox.sh steam          # launch sandbox Steam
./tools/fst-sandbox.sh check-cef      # query sandbox Steam, not host Steam
./tools/fst-sandbox.sh doctor         # inspect effective isolation and desktop health
./tools/fst-sandbox.sh shell          # interactive sandbox shell
./tools/fst-sandbox.sh exec dotnet --info
./tools/fst-sandbox.sh export ./sandbox-release  # new host output directory
./tools/fst-sandbox.sh stop
./tools/fst-sandbox.sh start
```

Edit files in your normal editor. Each `build` or `test` synchronizes the checkout into `/home/dev/work`; `.git`, `.env*`, bin/obj and release outputs are excluded. The checkout is mounted read-only at `/source`. Build outputs, NuGet cache, Steam, FST settings and downloaded games stay in the named volume `fst-sandbox-home`. Changes made to the internal source copy can be overwritten by the next sync; edit on the host.

On SELinux hosts, the source bind mount uses `relabel=shared` so both the development container and a separate test container can read the checkout under enforcing policy. Podman recursively changes the checkout's SELinux labels on the host, including `.git`; the mount remains read-only inside the container. Use a dedicated checkout if its current labels must be preserved. Existing containers need `remove` followed by `create` to pick up this mount option; their HOME volume is preserved. The Ubuntu CI run does not validate SELinux enforcement; that requires a separate run on an enforcing host.

`create` builds the image; it does not replace an existing container. To apply image/launcher changes, run `remove`, then `create`; the HOME volume survives. The base Arch image is rolling; SLSsteam, lumalinux, noVNC and websockify versions are selected in `tools/sandbox/Containerfile`. Software rendering is used, so this is intended for FST/Steam integration testing, not measuring game performance. No host GPU or audio devices are passed through.

## Isolation and limits

- Rootless, non-root container with all capabilities dropped and privilege escalation disabled.
- Private PID, IPC and network namespaces; read-only root filesystem, temporary runtime directories.
- Only the repository is bind-mounted, read-only. No host HOME, `/run/host`, X11/Wayland, D-Bus, keyring, Podman/Docker socket or GPU device is exposed.
- A separate X server and D-Bus run inside the container. Only the authenticated desktop is published, on **127.0.0.1**.
- Pasta host-loopback and host-address mappings are disabled. Outbound Internet and LAN access remain available; this is not a network firewall.
- The container shares the host kernel. This is **not a 100% security boundary** for hostile code; use a VM on a host with virtualization for that requirement. The goal here is to isolate development dependencies and prevent normal FST/Steam operations from modifying the host profile.

See [Podman's isolation and networking options](https://docs.podman.io/en/latest/markdown/podman-run.1.html).

## Automated checks and CI

`.github/workflows/linux-sandbox.yml` runs on pushes to main and pull requests using [Ubuntu 26.04](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2604-Readme.md). It installs container tools on the disposable GitHub runner, then uses the same launcher as local development to create a fresh HOME volume. No Steam or Ryuu account credentials are needed.

The default run checks shell scripts with ShellCheck, effective mount/capability/namespace isolation, a live host-loopback canary, compilation, core regression tests, Linux self-tests with both the offline fixture and the image's installed lumalinux CLI, the Arch package, GUI rendering, and profile persistence after stop/start. The upstream CLI tests deploy synthetic game data into temporary directories and verify VDF key injection and ACF recovery in a secondary library without Steam/Ryuu credentials. A missing CLI fails the sandbox test instead of being hidden by the fixture. Logs and the rendered FST PNG are uploaded as a seven-day artifact, including diagnostics on failure. The disposable container, image tag and HOME volume are cleaned up on script exit. The runner VM is discarded if the job is forcibly cancelled.

Reproduce it locally:

```bash
./tools/Test-Linux-Sandbox.sh
```

The script always generates separate resource names and requests an available localhost port; it does not reuse or delete your development sandbox. Results get a separate run folder under `analysis/sandbox-ci/`; override with `FST_SANDBOX_RESULTS=/absolute/path`.

For a fresh Steam download and an actual CEF response, run the workflow manually with **steam_smoke** enabled, or use:

```bash
./tools/Test-Linux-Sandbox.sh --steam
```

The Steam variant requires at least 12 GiB of free space in Podman storage, in addition to any existing development sandbox. This allows up to 15 minutes for Steam to update and start its web helper, without logging in. It depends on Valve's servers and is deliberately optional for pull requests. It does not test Steam account login, Discord/Ryuu authorization, real game downloads, hardware graphics or every FST interaction. Those still require manual testing with a test account.

A passing local run does not prove that a GitHub-hosted runner has passed; verify the first Actions run after pushing the commit. Arch package repositories and Steam updates are external, changing dependencies, so a future clean build can fail even if the source is unchanged.

## Cleanup and the previous Distrobox

```bash
./tools/fst-sandbox.sh remove   # remove container, preserve image and profile
./tools/fst-sandbox.sh purge    # delete container, image and profile; type DELETE
```

The former `fst-dev` Distrobox and `${XDG_DATA_HOME:-$HOME/.local/share}/fst-devbox/home` are not reused or deleted automatically. Its configuration exposed the host HOME and `/run/host` read-write, even with a separate HOME setting. Keep it stopped. If you need to remove it after saving any useful files, use `distrobox rm fst-dev`. `distrobox.ini` is retired; `fst-devbox.sh` forwards old commands to the new sandbox (`enter` becomes `shell`).

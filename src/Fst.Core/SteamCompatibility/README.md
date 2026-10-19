# Bundled Steam compatibility metadata

This snapshot contains the two signature tables and IPC metadata for the exact
Steam DLL SHA-256 hashes reported in the installation failure on 2026-10-19:

- `steamclient64.dll`: `e9045710ec7ee47d7b7488d1c5dda2d933dec7e251e965967a5c48f4df68eb4e`
- `steamui.dll`: `1b9457943205dc3af0bd53463ba331cbd2a381388699705c02271aac7a2b1252`

Data originates from [madoiscool/steam-monitor](https://github.com/madoiscool/steam-monitor).
`sources.json` records the immutable upstream commit URLs, sizes and SHA-256 of
the downloaded TOML files. The snapshot was also compared with the working
local Steam loader cache; all three files matched byte for byte.

FST embeds these files in Fst.Core. A valid existing cache takes priority;
otherwise an exact DLL hash match uses the bundled metadata before trying
network mirrors. Other Steam builds still use the network lookup. No metadata
from a different hash or component is substituted. The normal TOML validation,
DLL change checks and atomic cache installation also apply to bundled data.

To update the snapshot, download the relevant files from immutable upstream
commits, update the provenance manifest, validate their TOML and exercise a
fresh Steam fixture with network access rejected. Keep TOML line endings LF to
preserve the recorded content hashes.

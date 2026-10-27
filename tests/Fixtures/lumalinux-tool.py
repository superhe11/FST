"""Offline CLI contract fixture, executed only by --self-test."""
import argparse
from pathlib import Path

p = argparse.ArgumentParser()
p.add_argument('lua', type=Path)
p.add_argument('--manifests-dir', type=Path, required=True)
p.add_argument('--steam-root', type=Path, required=True)
p.add_argument('--sls-config', type=Path, required=True)
p.add_argument('--luma-keys', type=Path, required=True)
p.add_argument('--pin', action='store_true')
a = p.parse_args()
# Fixture is intentionally fixed to the test archive; it tests FST's CLI
# arguments, receipt and transaction, not a second implementation of upstream.
assert a.lua.name == '440.lua'
assert a.lua.read_text().startswith('addappid(440)\n')
assert (a.manifests_dir / '441_123.manifest').exists()
text = a.sls_config.read_text()
if '  - 440\n' not in text:
    text = text.replace('AdditionalApps:\n', 'AdditionalApps:\n  - 440\n')
if a.pin and '  778:' not in text:
    text = text.replace('ManifestIds:\n', 'ManifestIds:\n  778: 456\n')
a.sls_config.write_text(text)
a.luma_keys.write_text(a.luma_keys.read_text() + '\n440;\n441;440;' + ('123' if a.pin else '0') + ';0;' + 'a'*64 + '\n778;440;' + ('456' if a.pin else '0') + ';0;' + 'b'*64 + '\n')
for manifest in a.manifests_dir.glob('*.manifest'):
    (a.steam_root / 'depotcache' / manifest.name).write_bytes(manifest.read_bytes())
# Simulate a real upstream deletion in auto-update mode.
if not a.pin:
    (a.steam_root / 'depotcache/441_999.manifest').unlink(missing_ok=True)
lua = a.steam_root / 'config/stplug-in/440.lua'
lua.parent.mkdir(parents=True, exist_ok=True)
lua.write_text(a.lua.read_text() if a.pin else a.lua.read_text().replace('setManifestid', '-- setManifestid'))

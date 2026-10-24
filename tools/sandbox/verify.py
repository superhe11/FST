#!/usr/bin/env python3
"""Check the actual Podman configuration, not just the launcher's arguments."""
import json
import pathlib
import os
import socket
import subprocess
import threading
import sys

container, = json.load(sys.stdin)
config = container['HostConfig']
assert not config['Privileged'], 'Privileged container'
assert config['ReadonlyRootfs'], 'Writable root filesystem'
assert config['PidMode'] == 'private', 'Shared process namespace'
assert config['IpcMode'] == 'private', 'Shared IPC namespace'
assert config['NetworkMode'] == 'pasta', 'Unexpected network'
assert not config.get('Devices'), 'Host devices exposed'
assert 'no-new-privileges' in config['SecurityOpt'], 'Privilege escalation allowed'
assert not container['EffectiveCaps'], 'Container retains capabilities'
mounts = {m['Destination']: m for m in container['Mounts']}
assert set(mounts) == {'/source', '/home/dev'}, f'Unexpected mounts: {mounts.keys()}'
assert mounts['/source']['Source'] == str(pathlib.Path(sys.argv[1]).resolve())
assert not mounts['/source']['RW'], 'Host source is writable'
assert mounts['/home/dev']['Type'] == 'volume', 'HOME is not an isolated volume'
ports = config['PortBindings']
assert set(ports) == {'6080/tcp'}, 'Unexpected published ports (CEF must remain private)'
assert all(p['HostIp'] == '127.0.0.1' for p in ports['6080/tcp']), 'Desktop exposed outside loopback'
print('PASS: mount allowlist, namespaces, capabilities, read-only root, localhost-only desktop')

# Compare real namespaces, then use a live host-loopback canary. Testing only
# the flags would miss an engine/network-backend configuration regression.
for namespace in ('pid', 'net', 'ipc'):
    inside = subprocess.check_output(
        ['podman', 'exec', container['Name'], 'readlink', f'/proc/self/ns/{namespace}'],
        text=True).strip()
    assert inside != os.readlink(f'/proc/self/ns/{namespace}'), f'Shared {namespace} namespace'
with socket.socket() as listener:
    listener.bind(('127.0.0.1', 0))
    listener.listen()
    listener.settimeout(10)
    port = listener.getsockname()[1]
    def respond():
        try:
            while True:
                connection, _ = listener.accept()
                with connection:
                    connection.sendall(b'HTTP/1.0 200 OK\r\nContent-Length: 15\r\n\r\nFST-HOST-CANARY')
        except OSError:
            pass
    threading.Thread(target=respond, daemon=True).start()
    for address in ('127.0.0.1', '169.254.1.2'):
        result = subprocess.run(
            ['podman', 'exec', container['Name'], 'curl', '--noproxy', '*', '-fsS',
             '--max-time', '2', f'http://{address}:{port}'],
            capture_output=True, text=True)
        assert 'FST-HOST-CANARY' not in result.stdout, f'Host loopback reachable via {address}'
print('PASS: distinct PID/network/IPC namespaces; host loopback canary inaccessible')

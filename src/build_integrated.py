"""Build a signed bridge app from stable sources and an existing compatible base."""

import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import uuid
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent / "tools/MgbaSocketFix"))
from patch_mgba import patch_binary

ROOT = Path(__file__).resolve().parent
VENDOR = ROOT / 'vendor'


def bundle_string(value):
    encoded = value.encode('utf-8')
    size = len(encoded)
    prefix = bytearray()
    while size >= 128:
        prefix.append((size & 127) | 128)
        size >>= 7
    prefix.append(size)
    return bytes(prefix) + encoded


def find_dotnet(explicit):
    candidate = explicit or os.environ.get('PKHEX_DOTNET')
    if candidate:
        path = Path(candidate).expanduser().resolve()
    else:
        path = ROOT / '.tools/dotnet/dotnet'
        if not path.is_file():
            available = shutil.which('dotnet')
            if not available:
                raise SystemExit('Set PKHEX_DOTNET or pass --dotnet pointing to a .NET 10 SDK.')
            path = Path(available).resolve()
    if not path.is_file():
        raise SystemExit(f'.NET executable missing: {path}')
    return path


def run(command):
    subprocess.run([str(value) for value in command], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', type=Path)
    parser.add_argument('--mgba-base', type=Path, default=VENDOR / 'mGBA-0.10.5-base.app')
    parser.add_argument('--base-app', type=Path, default=VENDOR / 'PKHeX中文兼容-base.app')
    parser.add_argument('--output-root', type=Path, default=ROOT / 'builds')
    args = parser.parse_args()
    dotnet = find_dotnet(args.dotnet)
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S') + '-' + uuid.uuid4().hex[:8]
    output = args.output_root.expanduser().resolve() / stamp
    output.mkdir(parents=True)
    os.environ['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    os.environ['DOTNET_NOLOGO'] = '1'
    for project, folder in [
            (ROOT / 'PKHeX.GbaBridge.csproj', 'bridge'),
            (ROOT / 'tools/Patcher/Patcher.csproj', 'patcher'),
            (ROOT / 'tools/ChineseCompat/ChineseCompat.csproj', 'codec')]:
        run([dotnet, 'build', project, '-c', 'Release', '--nologo', '-o', output / folder])

    patcher = output / 'patcher/Patcher.dll'
    bridge = output / 'bridge/PKHeX.GbaBridge.dll'
    codec = output / 'codec/PKHeX.ChineseCompat.dll'
    patched = output / 'patched'
    patched.mkdir()
    run([dotnet, patcher, 'bridge', VENDOR / 'refs/PKHeX.Avalonia.dll', bridge, patched / 'PKHeX.Avalonia.dll'])
    run([dotnet, patcher, VENDOR / 'PKHeX.Core.original.dll', codec, patched / 'PKHeX.Core.dll'])
    run([dotnet, patcher, 'saveguard', VENDOR / 'refs/PKHeX.Infrastructure.dll', bridge, patched / 'PKHeX.Infrastructure.dll'])
    shutil.copy2(codec, patched / codec.name)

    target = output / 'PKHeX中文兼容.app'
    base_app = args.base_app.expanduser().resolve()
    source_manifest = json.loads((VENDOR / 'source-manifest.json').read_text())
    expected = source_manifest['PKHeX中文兼容-base.app/Contents/MacOS/PKHeX.Avalonia']
    actual = hashlib.sha256((base_app / 'Contents/MacOS/PKHeX.Avalonia').read_bytes()).hexdigest()
    if actual != expected:
        raise SystemExit('Base executable differs from the bundle manifest. Regenerate matching base inputs first.')
    shutil.copytree(base_app, target)
    executable = target / 'Contents/MacOS/PKHeX.Avalonia'
    run(['codesign', '--remove-signature', executable])
    data = bytearray(executable.read_bytes())
    manifest = json.loads((VENDOR / 'bundle.json').read_text())
    deps = json.loads((VENDOR / 'original.deps.json').read_text())
    for libraries in deps['targets'].values():
        app = next((value for key, value in libraries.items() if key.startswith('PKHeX.Avalonia/')), None)
        if app is None:
            continue
        app.setdefault('dependencies', {})['PKHeX.GbaBridge'] = '1.0.0'
        libraries['PKHeX.GbaBridge/1.0.0'] = {'runtime': {'PKHeX.GbaBridge.dll': {}}}
    deps['libraries']['PKHeX.GbaBridge/1.0.0'] = {'type': 'project', 'serviceable': False, 'sha512': ''}
    replacements = {
        'PKHeX.Avalonia.dll': (patched / 'PKHeX.Avalonia.dll').read_bytes(),
        'PKHeX.Core.dll': (patched / 'PKHeX.Core.dll').read_bytes(),
        'PKHeX.Infrastructure.dll': (patched / 'PKHeX.Infrastructure.dll').read_bytes(),
        'PKHeX.ChineseCompat.dll': codec.read_bytes(),
        'PKHeX.GbaBridge.dll': bridge.read_bytes(),
        'PKHeX.Avalonia.deps.json': json.dumps(deps, ensure_ascii=False).encode('utf-8'),
    }
    entries = []
    locations = list(manifest['locs'])
    for name, offset, size, compressed, kind in manifest['entries']:
        if name in replacements:
            payload = replacements.pop(name)
            data.extend(b'\0' * (-len(data) % 4096))
            offset, size, compressed = len(data), len(payload), 0
            data.extend(payload)
            if name.endswith('.deps.json'):
                locations[:2] = [offset, size]
        entries.append((name, offset, size, compressed, kind))
    for name, payload in replacements.items():
        data.extend(b'\0' * (-len(data) % 4096))
        offset = len(data)
        data.extend(payload)
        entries.append((name, offset, len(payload), 0, 1))
    header = len(data)
    identity = 'BR' + hashlib.sha256(repr(entries).encode()).hexdigest()[:14]
    data.extend(struct.pack('<III', manifest['major'], manifest['minor'], len(entries)))
    data.extend(bundle_string(identity))
    data.extend(struct.pack('<5Q', *locations))
    for name, offset, size, compressed, kind in entries:
        data.extend(struct.pack('<QQQB', offset, size, compressed, kind))
        data.extend(bundle_string(name))
    executable.write_bytes(data)
    run([dotnet, patcher, 'bundle', executable, header])
    resources = target / 'Contents/Resources/GBABridge'
    resources.mkdir(exist_ok=True)
    shutil.copy2(ROOT / 'Resources/mgba_bridge.lua', resources / 'mgba_bridge.lua')
    shutil.copy2(ROOT / 'Resources/save_commit.lua', resources / 'save_commit.lua')
    info_path = target / 'Contents/Info.plist'
    with info_path.open('rb') as handle:
        info = plistlib.load(handle)
    info['CFBundleShortVersionString'] = '1.101.2-cn4-bridge'
    with info_path.open('wb') as handle:
        plistlib.dump(info, handle)
    run(['codesign', '--force', '--sign', '-', '--identifier', info['CFBundleIdentifier'], target])
    run(['codesign', '--verify', '--strict', target])
    emulator = output / 'mGBA.app'
    emulator_source = args.mgba_base.expanduser().resolve()
    emulator_expected = source_manifest['mGBA-0.10.5-base.app/Contents/MacOS/mGBA']
    if hashlib.sha256((emulator_source / 'Contents/MacOS/mGBA').read_bytes()).hexdigest() != emulator_expected:
        raise SystemExit('mGBA base differs from the validated native patch input.')
    shutil.copytree(emulator_source, emulator)
    emulator_binary = emulator / 'Contents/MacOS/mGBA'
    run(['codesign', '--remove-signature', emulator_binary])
    emulator_fix = patch_binary(emulator_binary)
    emulator_info_path = emulator / 'Contents/Info.plist'
    with emulator_info_path.open('rb') as handle:
        emulator_info = plistlib.load(handle)
    emulator_info['PKHeXSocketCloseFix'] = 3
    with emulator_info_path.open('wb') as handle:
        plistlib.dump(emulator_info, handle)
    run(['codesign', '--force', '--sign', '-', '--identifier', emulator_info['CFBundleIdentifier'], emulator])
    run(['codesign', '--verify', '--strict', emulator])
    (output / 'build-info.json').write_text(json.dumps({
        'app': str(target), 'emulator': str(emulator), 'emulator_fix': emulator_fix, 'dotnet': str(dotnet), 'protocol': 3,
        'dotnet_version': subprocess.check_output([str(dotnet), '--version'], text=True).strip(),
        'source_hashes': {
            str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in [ROOT / 'BridgeEngine.cs', ROOT / 'BridgeModule.cs', ROOT / 'SaveCommitCoordinator.cs',
                         ROOT / 'Resources/mgba_bridge.lua', ROOT / 'Resources/save_commit.lua', ROOT / 'tools/Patcher/Program.cs',
                         ROOT / 'tools/ChineseCompat/ChineseCodec.cs', ROOT / 'tools/ChineseCompat/ChineseMap.tsv',
                         ROOT / 'build_integrated.py', ROOT / 'tools/MgbaSocketFix/patch_mgba.py', ROOT / 'tools/MgbaSocketFix/socket-close.patch']
        },
    }, ensure_ascii=False, indent=2))
    print('Integrated app built', target)


if __name__ == '__main__':
    main()

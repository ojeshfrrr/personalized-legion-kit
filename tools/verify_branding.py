"""Verify native icon frames and copied branding without starting the application."""
import hashlib
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()

def verify(output):
    icon = (ROOT / 'assets/branding/project-legion.ico').read_bytes()
    assert struct.unpack_from('<HHH', icon) == (0, 1, len(SIZES))
    assert [icon[6 + i * 16] or 256 for i in range(len(SIZES))] == SIZES
    for relative in ['LenovoLegionToolkit.WPF/Assets/ProjectLegion.ico', 'InnoDependencies/Images/setup_icon.ico']:
        assert (ROOT / relative).read_bytes() == icon, relative
    copied = []
    for source in sorted((ROOT / 'assets/branding').rglob('*')):
        if source.suffix not in ('.svg', '.png', '.ico') and source.name != 'README.md':
            continue
        target = output / 'Branding' / source.relative_to(ROOT / 'assets/branding')
        assert target.is_file() and sha(source) == sha(target), str(target)
        copied.append(str(target.relative_to(output)))
    # Inspect the PE resource tree; match every native icon payload, not just its dimensions.
    exe = output / 'Project Legion.exe'
    data = exe.read_bytes()
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    count = struct.unpack_from('<H', data, pe + 6)[0]
    opt = pe + 24
    opt_size = struct.unpack_from('<H', data, pe + 20)[0]
    dirs = opt + (112 if struct.unpack_from('<H', data, opt)[0] == 0x20b else 96)
    resource_rva = struct.unpack_from('<I', data, dirs + 16)[0]
    sections = [struct.unpack_from('<IIII', data, opt + opt_size + i * 40 + 8) for i in range(count)]
    def offset(rva):
        for virtual_size, virtual_address, raw_size, raw_address in sections:
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_address + rva - virtual_address
        raise AssertionError('Unmapped RVA')
    base = offset(resource_rva)
    def entries(relative):
        pos = base + relative
        named, ids = struct.unpack_from('<HH', data, pos + 12)
        return dict(struct.unpack_from('<II', data, pos + 16 + i * 8) for i in range(named + ids))
    def payload(relative):
        leaf = next(iter(entries(relative & 0x7fffffff).values()))
        rva, size = struct.unpack_from('<II', data, base + leaf)
        return data[offset(rva):offset(rva) + size]
    root = entries(0)
    native_icons = {key: payload(value) for key, value in entries(root[3] & 0x7fffffff).items()}
    groups = [payload(value) for value in entries(root[14] & 0x7fffffff).values()]
    assert len(groups) == 1
    group = groups[0]
    assert struct.unpack_from('<HHH', group) == (0, 1, len(SIZES))
    for i, size in enumerate(SIZES):
        width, height, _, _, _, bits, length, icon_id = struct.unpack_from('<BBBBHHIH', group, 6 + i * 14)
        assert (width or 256, height or 256, bits) == (size, size, 32)
        expected_length, start = struct.unpack_from('<II', icon, 6 + i * 16 + 8)
        assert length == expected_length and native_icons[icon_id] == icon[start:start + length]
    return {'output': str(output), 'executableSha256': sha(exe), 'verifiedNativeIconSizes': SIZES,
            'verifiedBrandingFiles': len(copied), 'applicationLaunched': False}

if __name__ == '__main__':
    print(json.dumps(verify(Path(sys.argv[1]).resolve()), indent=2))

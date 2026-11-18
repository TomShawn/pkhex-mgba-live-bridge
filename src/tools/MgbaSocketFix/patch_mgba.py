"""Apply the mGBA 0.10.5 arm64 socket-close fix with checked Mach-O inputs.

The original 16-byte leaf cannot hold the additional store. A checked unused
__TEXT header-padding region contains the equivalent fixed leaf implementation.
"""

from pathlib import Path
import struct
import subprocess

NOP = 0xD503201F
RET = 0xD65F03C0


def branch(source, target):
    delta = target - source
    if delta % 4 or not -(1 << 27) <= delta < (1 << 27):
        raise ValueError('ARM64 branch target out of range')
    return 0x14000000 | ((delta // 4) & 0x3FFFFFF)


def patch_binary(path):
    path = Path(path)
    data = bytearray(path.read_bytes())
    base = 0
    if data[:4] == bytes.fromhex('cafebabe'):
        if struct.unpack_from('>I', data, 4)[0] != 1:
            raise ValueError('Expected the single-arm64 mGBA package')
        cpu, _, base, _, _ = struct.unpack_from('>IIIII', data, 8)
        if cpu != 0x100000C:
            raise ValueError('Expected arm64 mGBA')
    if data[base:base + 4] != bytes.fromhex('cffaedfe'):
        raise ValueError('Expected a 64-bit little-endian Mach-O')

    symbols = subprocess.check_output(['nm', '-n', str(path)], text=True)
    matches = [int(line.split()[0], 16) for line in symbols.splitlines()
               if line.split()[-1:] == ['__mScriptSocketClose']]
    if len(matches) != 1:
        raise ValueError('Socket close symbol is missing or ambiguous')
    address = matches[0]
    bindings = [int(line.split()[0],16) for line in symbols.splitlines()
                if line.split()[-1:] == ['__mSTStructBinding_mScriptSocket_close']]
    if len(bindings) != 1:
        raise ValueError('Socket binding symbol is missing or ambiguous')
    binding_address = bindings[0]
    command_count, command_size = struct.unpack_from('<II', data, base + 16)
    position = base + 32
    text = None
    sections = []
    for _ in range(command_count):
        command, size = struct.unpack_from('<II', data, position)
        if command == 0x19:
            name = bytes(data[position + 8:position + 24]).split(b'\0')[0]
            if name == b'__TEXT':
                vm, vm_size, file_offset, file_size = struct.unpack_from('<QQQQ', data, position + 24)
                protections = struct.unpack_from('<II', data, position + 56)
                if protections != (5, 5):
                    raise ValueError('Unexpected executable segment permissions')
                text = (vm, file_offset, file_size)
                count = struct.unpack_from('<I', data, position + 64)[0]
                for index in range(count):
                    section = position + 72 + index * 80
                    section_name = bytes(data[section:section + 16]).split(b'\0')[0]
                    section_address, section_size, offset = struct.unpack_from('<QQI', data, section + 32)
                    if section_size and offset:
                        sections.append((section_name, section_address, section_size, offset))
        position += size
    if text is None or not sections:
        raise ValueError('Executable text layout missing')
    vm, file_offset, file_size = text
    offset = base + file_offset + address - vm
    original = struct.unpack_from('<4I', data, offset)
    if (original[0] != 0xB9400000 or original[1] != 0x37F80040
            or original[2] & 0xFC000000 != 0x14000000 or original[3] != RET):
        raise ValueError('Socket close implementation differs from validated mGBA 0.10.5')
    immediate = original[2] & 0x3FFFFFF
    if immediate & 0x2000000:
        immediate -= 0x4000000
    close_target = address + 8 + immediate * 4
    if not vm <= close_target < vm + file_size:
        raise ValueError('Original close target lies outside executable text')

    cave_offset = min(item[3] for item in sections) - 64
    if cave_offset < 32 + command_size:
        raise ValueError('Executable header padding is too small')
    if data[base + cave_offset:base + cave_offset + 64] != b'\0' * 64:
        raise ValueError('Executable padding is already in use')
    cave_address = vm + cave_offset - file_offset
    # fd = socket->socket. Negative fds preserve the original no-op behavior.
    # The descriptor becomes INVALID_SOCKET before tail-calling close(fd).
    fixed = [
        0x37F800A8,             # tbnz w8, #31, return
        0x12800009,             # mov w9, #-1
        0xB9000009,             # str w9, [x0]
        0x2A0803E0,             # mov w0, w8
        branch(cave_address + 16, close_target),
        RET,
    ]
    replacement = [0xB9400008, branch(address + 4, cave_address), NOP, NOP]
    struct.pack_into('<4I', data, offset, *replacement)
    struct.pack_into('<6I', data, base + cave_offset, *fixed)
    # Clang also inlined the original close into the script binding. Route that
    # verified call site through the fixed leaf, including destructor invocations.
    binding_offset = base + file_offset + binding_address - vm
    candidates = []
    for relative in range(0,0xD0 - 12 + 1,4):
        a, b, c = struct.unpack_from('<3I',data,binding_offset + relative)
        call_address = binding_address + relative + 8
        if a == 0xB9400280 and b == 0x37F80040 and c == (branch(call_address,close_target) | 0x80000000):
            candidates.append(relative)
    if len(candidates) != 1:
        raise ValueError('Inlined socket close differs from validated implementation')
    relative = candidates[0]
    binding_offset += relative
    binding_address += relative
    binding_original = bytes(data[binding_offset:binding_offset + 12])
    binding_replacement = struct.pack('<3I',0xAA1403E0,branch(binding_address + 4,address) | 0x80000000,NOP)
    data[binding_offset:binding_offset + 12] = binding_replacement
    receive_checks = []
    for symbol, span in [('__mScriptSocketRecv',0x84), ('__mSTStructBinding_mScriptSocket_recv',0x198)]:
        found = [int(line.split()[0],16) for line in symbols.splitlines() if line.split()[-1:] == [symbol]]
        if len(found) != 1:
            raise ValueError('Receive symbol is missing or ambiguous')
        start = base + file_offset + found[0] - vm
        sites = []
        for relative in range(0,span - 8,4):
            compare, condition = struct.unpack_from('<II',data,start + relative)
            if compare == 0xF100001F and condition & 0xFF00001F == 0x5400000D:
                sites.append(relative)
        if len(sites) != 1:
            raise ValueError('Receive EOF branch differs from validated implementation')
        relative = sites[0]
        offset_of_branch = start + relative + 4
        original_branch = struct.unpack_from('<I',data,offset_of_branch)[0]
        fixed_branch = (original_branch & ~0xF) | 0xB
        struct.pack_into('<I',data,offset_of_branch,fixed_branch)
        receive_checks.append({'address':hex(found[0] + relative + 4),'file_offset':offset_of_branch,
                               'original':original_branch,'replacement':fixed_branch})
    path.write_bytes(data)
    return {
        'receive_checks': receive_checks, 'symbol': '__mScriptSocketClose', 'address': hex(address),
        'file_offset': offset, 'padding_offset': base + cave_offset,
        'padding_address': hex(cave_address), 'close_target': hex(close_target),
        'binding_address': hex(binding_address), 'binding_offset': binding_offset,
        'binding_original': binding_original.hex(), 'binding_replacement': binding_replacement.hex(),
        'original': struct.pack('<4I', *original).hex(),
        'replacement': struct.pack('<4I', *replacement).hex(),
        'fixed_leaf': struct.pack('<6I', *fixed).hex(),
    }

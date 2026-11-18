#!/usr/bin/env python3
"""Synchronize PKHeX save edits into the running Emerald emulator."""

from __future__ import annotations

import argparse
import dataclasses
import datetime
import fcntl
import itertools
import hashlib
import json
import os
from pathlib import Path
import secrets
import signal
import socket
import struct
import subprocess
import sys
import time
import zlib

ROOT = Path(__file__).resolve().parent
DEFAULT_ROM = Path(os.environ.get(
    'GBA_BRIDGE_ROM',
    str(Path.home() / 'Downloads/GBA口袋妖怪绿宝石386Hack版游戏合集/GBA口袋妖怪绿宝石386汉化版.gba'),
))
MGBA = Path('/Applications/mGBA.app/Contents/MacOS/mGBA')
PKHEX = Path('/Applications/PKHeX.Avalonia.app')
PKHEX_CHINESE = Path('/Applications/PKHeX中文兼容.app')
SIZES = (0xF2C, 0xF80, 0xF80, 0xF80, 0xF08, *([0xF80] * 8), 0x7D0)
PERMUTATIONS = tuple(itertools.permutations(range(4)))
PARTY_ADDRESS = 0x020244EC
PARTY_COUNT = 0x020244E9
running = True


class BridgeError(Exception):
    """An invalid save, conflicting edit, or unavailable emulator."""


def log(message: str) -> None:
    print(f'[{datetime.datetime.now():%H:%M:%S}] {message}', flush=True)


def checksum(data: bytes) -> int:
    total = sum(struct.unpack(f'<{len(data) // 4}I', data)) & 0xFFFFFFFF
    return ((total & 0xFFFF) + (total >> 16)) & 0xFFFF


@dataclasses.dataclass
class Save:
    small: bytes
    large: bytes
    boxes: bytes
    counter: int = 0

    @classmethod
    def parse(cls, data: bytes, *, require_latest_complete: bool = False) -> Save:
        if len(data) not in (0x20000, 0x20010):
            raise BridgeError('需要 128 KiB 的游戏内存档，可附带 mGBA 的 16 字节时钟数据。')
        candidates = []
        for start in (0, 14 * 4096):
            sections = {}
            counters = set()
            for offset in range(start, start + 14 * 4096, 4096):
                number, check, signature, counter = struct.unpack_from('<HHII', data, offset + 0xFF4)
                if number >= 14 or signature != 0x08012025 or number in sections:
                    break
                payload = data[offset:offset + SIZES[number]]
                if checksum(payload) != check:
                    break
                sections[number] = payload
                counters.add(counter)
            else:
                if len(sections) == 14 and len(counters) == 1:
                    candidates.append((counters.pop(), sections))
        if not candidates:
            raise BridgeError('存档区校验未通过，等待文件写入完成。')
        chosen = candidates[0]
        for candidate in candidates[1:]:
            difference = (candidate[0] - chosen[0]) & 0xFFFFFFFF
            if 0 < difference < 0x80000000:
                chosen = candidate
        if require_latest_complete:
            for offset in range(0, 28 * 4096, 4096):
                number, _, signature, counter = struct.unpack_from('<HHII', data, offset + 0xFF4)
                difference = (counter - chosen[0]) & 0xFFFFFFFF
                if signature == 0x08012025 and number < 14 and 0 < difference < 0x80000000:
                    raise BridgeError('较新的存档区仍在写入，等待完整保存。')
        sections = chosen[1]
        return cls(sections[0], b''.join(sections[i] for i in range(1, 5)), b''.join(sections[i] for i in range(5, 14)), chosen[0])


def uncrypt_mon(data: bytes) -> bytearray:
    """Return header + logical G/A/E/M blocks + optional party data."""
    pid, trainer = struct.unpack_from('<II', data)
    key = pid ^ trainer
    decrypted = b''.join(struct.pack('<I', struct.unpack_from('<I', data, i)[0] ^ key) for i in range(32, 80, 4))
    if data[19] & 2 and sum(struct.unpack('<24H', decrypted)) & 0xFFFF != struct.unpack_from('<H', data, 28)[0]:
        raise BridgeError('宝可梦记录校验未通过，请先核对该存档。')
    order = PERMUTATIONS[pid % 24]
    blocks = [b''] * 4
    for position, logical in enumerate(order):
        blocks[logical] = decrypted[position * 12:(position + 1) * 12]
    return bytearray(data[:32] + b''.join(blocks) + data[80:])


def crypt_mon(data: bytearray) -> bytes:
    pid, trainer = struct.unpack_from('<II', data)
    key = pid ^ trainer
    payload = b''.join(data[32 + logical * 12:44 + logical * 12] for logical in PERMUTATIONS[pid % 24])
    result = bytearray(data)
    struct.pack_into('<H', result, 28, sum(struct.unpack('<24H', payload)) & 0xFFFF)
    for index in range(0, 48, 4):
        struct.pack_into('<I', result, 32 + index, struct.unpack_from('<I', payload, index)[0] ^ key)
    return bytes(result)


MON_FIELDS = [(0, 4), (4, 4), (8, 10), (18, 1), (19, 1), (20, 7), (27, 1), (30, 2),
              (32, 2), (34, 2), (36, 4), (40, 1), (41, 1), (42, 2),
              *[(44 + i * 2, 2) for i in range(4)],
              *[(52 + i, 1) for i in range(4)],
              *[(56 + i, 1) for i in range(12)],
              (68, 1), (69, 1), (70, 2), (72, 4), (76, 4)]


def merge_mon(before: bytes, after: bytes, current: bytes) -> bytes:
    if before == after:
        return current
    # Empty slots and deliberate replacement require a complete record.
    if not before[19] & 2 or not after[19] & 2 or before[:8] != after[:8]:
        if current[:8] not in (before[:8], after[:8]):
            raise BridgeError('宝可梦槽位已在游戏内改变，请在 PKHeX 重新打开当前存档。')
        return after
    if current[:8] not in (before[:8], after[:8]):
        raise BridgeError('宝可梦身份与编辑时不一致，请在 PKHeX 重新打开当前存档。')
    old, new, live = map(uncrypt_mon, (before, after, current))
    fields = MON_FIELDS
    if len(before) == 100:
        fields = fields + [(80, 4), (84, 1), (85, 1), *[(i, 2) for i in range(86, 100, 2)]]
    for offset, size in fields:
        if old[offset:offset + size] != new[offset:offset + size]:
            live[offset:offset + size] = new[offset:offset + size]
    return crypt_mon(live)


def canonical(save: Save) -> Save:
    small, large = bytearray(save.small), bytearray(save.large)
    key = struct.unpack_from('<I', small, 0xAC)[0]
    small[0xAC:0xB0] = b'\0' * 4
    struct.pack_into('<I', small, 0x1F4, struct.unpack_from('<I', small, 0x1F4)[0] ^ key)
    for offset, width in ((0x490, 4), (0x494, 2), *[(i, 2) for i in range(0x562, 0x848, 4)], *[(i, 4) for i in range(0x159C, 0x169C, 4)]):
        fmt = '<I' if width == 4 else '<H'
        value = struct.unpack_from(fmt, large, offset)[0] ^ (key & ((1 << (width * 8)) - 1))
        struct.pack_into(fmt, large, offset, value)
    return Save(bytes(small), bytes(large), save.boxes)


def encode_with_key(save: Save, key: int) -> Save:
    small = bytearray(save.small)
    struct.pack_into('<I', small, 0xAC, key)
    # XOR transforms are symmetric. canonical() also clears the key field.
    result = canonical(Save(bytes(small), save.large, save.boxes))
    small = bytearray(result.small)
    struct.pack_into('<I', small, 0xAC, key)
    return Save(bytes(small), result.large, result.boxes)


def merge_region(before: bytes, after: bytes, current: bytes, fields: list[tuple[int, int]], bit_ranges=()) -> bytes:
    output = bytearray(current)
    handled = bytearray(len(before))
    for offset, size in fields:
        handled[offset:offset + size] = b'\1' * size
        if before[offset:offset + size] != after[offset:offset + size]:
            output[offset:offset + size] = after[offset:offset + size]
    for index, (old, new) in enumerate(zip(before, after)):
        if handled[index] or old == new:
            continue
        if any(start <= index < end for start, end in bit_ranges):
            mask = old ^ new
            output[index] = (current[index] & ~mask) | (new & mask)
        else:
            output[index] = new
    return bytes(output)


SMALL_FIELDS = [(0, 8), (8, 1), (10, 4), (14, 5), (19, 1), (20, 2), (0x1F4, 4)]
LARGE_FIELDS = [(0, 4), *[(i, 8) for i in range(4, 0x2C, 8)], (0x2C, 2), (0x32, 2),
                (0x234, 4), (0x238, 600), (0x490, 4), (0x494, 2), (0x496, 2),
                *[(i, 4) for i in range(0x498, 0x848, 4)],
                *[(i, 2) for i in range(0x139C, 0x159C, 2)],
                *[(i, 4) for i in range(0x159C, 0x169C, 4)]]


def changed_spans(before: bytes, after: bytes, address: int, fields=()):
    """Compare and replace complete semantic fields, including unchanged bytes."""
    covered = bytearray(len(before))
    for offset, size in fields:
        covered[offset:offset + size] = b'\1' * size
        if before[offset:offset + size] != after[offset:offset + size]:
            yield address + offset, before[offset:offset + size], after[offset:offset + size]
    offset = 0
    while offset < len(before):
        if covered[offset]:
            offset += 1
            continue
        end = offset + 1
        while end < len(before) and not covered[end]:
            end += 1
        # An unknown field can cross a word boundary. Guard the entire
        # surrounding unknown region, split only for the protocol size limit.
        if before[offset:end] != after[offset:end]:
            for start in range(offset, end, 256):
                stop = min(start + 256, end)
                yield address + start, before[start:stop], after[start:stop]
        offset = end


def merge_slots(before: bytes, after: bytes, current: bytes, width: int) -> bytes:
    old_slots = [before[i:i + width] for i in range(0, len(before), width)]
    new_slots = [after[i:i + width] for i in range(0, len(after), width)]
    live_slots = [current[i:i + width] for i in range(0, len(current), width)]
    output = []
    for old, new, live in zip(old_slots, new_slots, live_slots):
        if old[:8] != new[:8] and new[19] & 2:
            # A move between slots should carry the running Pokemon's current HP/PP.
            previous = [mon for mon in old_slots if mon[19] & 2 and mon[:8] == new[:8]]
            active = [mon for mon in live_slots if mon[19] & 2 and mon[:8] == new[:8]]
            if len(previous) == 1 and len(active) == 1:
                old, live = previous[0], active[0]
        output.append(merge_mon(old, new, live))
    return b''.join(output)


@dataclasses.dataclass
class Snapshot:
    frame: int
    large_address: int
    small_address: int
    box_address: int
    raw: Save
    party: bytes
    session: str = ""
    epoch: int = 0


def validate_edit_version(baseline: Save, updated: Save):
    if updated.counter != baseline.counter:
        raise BridgeError('编辑存档版本已过期。已阻止写入，请从最新游戏存档备份重新打开并合并待提交修改。')


def preserve_file(folder: Path, label: str, data: bytes) -> Path:
    folder.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256(data).hexdigest()[:20]
    target = folder / f'{label}_{digest}.sav'
    if not target.exists():
        with target.open('xb') as handle:
            handle.write(data)
    return target


def plan(before: Save, after: Save, snapshot: Snapshot):
    old, new, live = map(canonical, (before, after, snapshot.raw))
    if live.small[10:14] not in (old.small[10:14], new.small[10:14]):
        raise BridgeError('当前运行的训练家与编辑存档不匹配。')
    small = merge_region(old.small, new.small, live.small, SMALL_FIELDS, ((0x28, 0x90),))
    large = bytearray(merge_region(old.large, new.large, live.large, LARGE_FIELDS, ((0x988, 0x9BC), (0x1270, 0x139C), (0x3B24, 0x3B58))))
    old_party, new_party = old.large[0x238:0x490], new.large[0x238:0x490]
    count = snapshot.party[0]
    if old.large[0x234] != new.large[0x234]:
        count = new.large[0x234]
    if not 1 <= count <= 6:
        raise BridgeError('队伍数量需要为 1～6。')
    party = merge_slots(old_party, new_party, snapshot.party[1:], 100)
    large[0x234] = count
    large[0x238:0x490] = party
    boxes = bytearray(live.boxes)
    boxes[:4] = merge_region(old.boxes[:4], new.boxes[:4], live.boxes[:4], [(0, 4)])
    tail = 4 + 14 * 30 * 80
    boxes[4:tail] = merge_slots(old.boxes[4:tail], new.boxes[4:tail], live.boxes[4:tail], 80)
    boxes[tail:] = merge_region(old.boxes[tail:], new.boxes[tail:], live.boxes[tail:], [])
    current_key = struct.unpack_from('<I', snapshot.raw.small, 0xAC)[0]
    encoded = encode_with_key(Save(small, bytes(large), bytes(boxes)), current_key)
    patches = []
    large_fields = [field for field in LARGE_FIELDS if field != (0x238, 600)]
    large_fields.extend((0x238 + i * 100, 100) for i in range(6))
    box_fields = [(0, 4), *((4 + i * 80, 80) for i in range(420))]
    for previous, desired, address, fields in (
            (snapshot.raw.small, encoded.small, snapshot.small_address, SMALL_FIELDS),
            (snapshot.raw.large, encoded.large, snapshot.large_address, large_fields),
            (snapshot.raw.boxes, encoded.boxes, snapshot.box_address, box_fields),
            (snapshot.party[:1], bytes([count]), PARTY_COUNT, [(0, 1)]),
            (snapshot.party[1:], bytes(party), PARTY_ADDRESS, [(i * 100, 100) for i in range(6)])):
        patches.extend(changed_spans(previous, desired, address, fields))
    if patches:
        # Guard dependencies even when an edit does not change these bytes.
        patches.append((snapshot.small_address + 0xAC, snapshot.raw.small[0xAC:0xB0], snapshot.raw.small[0xAC:0xB0]))
        if snapshot.raw.small[10:14] == encoded.small[10:14]:
            patches.append((snapshot.small_address + 10, snapshot.raw.small[10:14], snapshot.raw.small[10:14]))
        if count == snapshot.party[0]:
            patches.append((PARTY_COUNT, snapshot.party[:1], snapshot.party[:1]))
    return patches


class StaleTransactionError(BridgeError):
    """The server session or emulated timeline changed."""


@dataclasses.dataclass
class PendingEdit:
    data: bytes
    updated: Save
    session: str
    epoch: int
    identity: str = dataclasses.field(default_factory=lambda: secrets.token_hex(16))


class Connection:
    def __init__(self, port: int, token: str, crc: str):
        self.socket = socket.create_connection(('127.0.0.1', port), timeout=4)
        self.socket.settimeout(6)
        self.reader = self.socket.makefile('rb')
        self.send(f'AUTH {token}')
        reply = self.line()
        fields = reply.split()
        if len(fields) != 5 or fields[:2] != ['HELLO', crc] or fields[4] != '3':
            self.close()
            raise BridgeError(f'模拟器身份校验失败 {reply}')

    def send(self, text: str):
        self.socket.sendall((text + '\n').encode('ascii'))

    def line(self):
        line = self.reader.readline(250000)
        if not line:
            raise ConnectionError('模拟器连接已断开')
        return line.decode('utf-8', errors='replace').strip()

    def snapshot(self) -> Snapshot:
        self.send('SNAP')
        header = self.line().split()
        if len(header) != 7 or header[0] != 'STATE':
            raise BridgeError('请先继续游戏并等待战斗结束。')
        payload = {}
        while (line := self.line()) != 'END':
            key, value = line.split(' ', 1)
            payload[key] = bytes.fromhex(value)
        expected_sizes = {'SMALL': 0xF2C, 'LARGE': 0x3D88, 'BOXES': 0x83D0, 'PARTY': 601}
        if any(len(payload.get(key, b'')) != size for key, size in expected_sizes.items()):
            raise ConnectionError('模拟器返回的数据不完整，正在重新连接。')
        return Snapshot(int(header[1]), *(int(value, 16) for value in header[2:5]), Save(payload['SMALL'], payload['LARGE'], payload['BOXES']), payload['PARTY'], header[5], int(header[6]))

    def status(self, pending: PendingEdit):
        self.send(f'STATUS {pending.identity} {pending.session} {pending.epoch}')
        response = self.line()
        if response == f'STALE {pending.identity}':
            raise StaleTransactionError('游戏会话或时间线已改变，待提交修改已隔离，请基于当前游戏重新编辑。')
        fields = response.split()
        if len(fields) == 4 and fields[:2] == ['APPLIED', pending.identity]:
            return response
        if response != f'UNKNOWN {pending.identity}':
            raise ConnectionError('事务查询回复无效。')
        return None

    def apply(self, snapshot: Snapshot, patches, pending: PendingEdit):
        completed = self.status(pending)
        if completed is not None:
            return completed
        if (snapshot.session, snapshot.epoch) != (pending.session, pending.epoch):
            raise StaleTransactionError('游戏时间线已变化，待提交修改已隔离。')
        transaction = pending.identity
        commands = [f'BEGIN {transaction} {pending.session} {pending.epoch} {snapshot.large_address:x} {snapshot.small_address:x} {snapshot.box_address:x}']
        commands.extend(f'PATCH {address:x} {before.hex()} {after.hex()}' for address, before, after in patches)
        commands.append(f'COMMIT {transaction}')
        self.send('\n'.join(commands))
        response = self.line()
        if response == f'STALE {transaction}':
            raise StaleTransactionError('游戏时间线已变化，待提交修改已隔离。')
        fields = response.split()
        if len(fields) != 4 or fields[:2] != ['APPLIED', transaction]:
            raise BridgeError(f'本次写入已延后，{response}')
        return response

    def close(self):
        self.reader.close()
        self.socket.close()


def serve(args):
    rom = args.rom.expanduser().resolve()
    save_path = (args.save or rom.with_suffix('.sav')).expanduser().resolve()
    rom_data = rom.read_bytes()
    if rom_data[0xAC:0xB0] != b'BPEE':
        raise BridgeError('此版本桥接适配美版底本的绿宝石，当前 ROM 标识不匹配。')
    baseline_bytes = save_path.read_bytes()
    baseline = Save.parse(baseline_bytes)
    folder = ROOT / '.runtime'
    folder.mkdir(exist_ok=True, mode=0o700)
    lock = (folder / 'bridge.lock').open('w')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError as error:
        raise BridgeError('已有一个桥接程序正在运行。') from error
    shared_folder = Path.home() / 'Library/Application Support/PKHeX-Avalonia/GBABridge'
    shared_folder.mkdir(parents=True, exist_ok=True)
    shared_lock = (shared_folder / 'bridge.lock').open('a')
    try:
        fcntl.flock(shared_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError as error:
        raise BridgeError('修改器内已有桥接正在运行，请先停止后再使用独立桥接。') from error
    token = secrets.token_hex(24)
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        port = probe.getsockname()[1]
    crc = f'{zlib.crc32(rom_data):08x}'
    boot = folder / '连接mGBA.lua'
    boot.write_text(f'GBA_BRIDGE_CONFIG = {{port={port}, token="{token}", crc="{crc}", save_path={json.dumps(str(save_path), ensure_ascii=False)}, save_module={json.dumps(str(ROOT / "save_commit.lua"), ensure_ascii=False)}}}\ndofile({json.dumps(str(ROOT / "mgba_bridge.lua"), ensure_ascii=False)})\n', encoding='utf-8')
    boot.chmod(0o600)
    endpoint = shared_folder / 'endpoint.json'
    endpoint.write_text(json.dumps({'Port': port, 'Token': token, 'Crc': crc, 'SavePath': str(save_path)}), encoding='utf-8')
    endpoint.chmod(0o600)
    backup_dir = ROOT / '存档备份'
    backup_dir.mkdir(exist_ok=True)
    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S-%f')
    (backup_dir / f'{save_path.stem}_{stamp}.sav').write_bytes(baseline_bytes)
    log(f'监视存档 {save_path}')
    log('在 mGBA 中继续游戏，然后打开 工具 → 脚本，运行下面一行。')
    print(f'dofile({json.dumps(str(boot), ensure_ascii=False)})', flush=True)
    log('在 PKHeX 中打开上面的同一个 .sav，修改后点“应用／设置”，再点“保存”。')
    log('脚本加载后可返回游戏。游戏内保存可将运行进度与修改一起写入存档。Ctrl+C 结束桥接。')
    if not args.no_launch:
        pkhex_app = PKHEX_CHINESE if PKHEX_CHINESE.is_dir() else PKHEX
        subprocess.Popen(['/usr/bin/open', '-a', str(pkhex_app)])
        if subprocess.run(['pgrep', '-f', '^/Applications/mGBA.app/Contents/MacOS/mGBA'], capture_output=True).returncode:
            subprocess.Popen(['/usr/bin/open', '-a', '/Applications/mGBA.app', str(rom)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        else:
            log(f'mGBA 已运行，请确认加载的是此路径的游戏 {rom}')
    connection = None
    last_message = None
    pending_data = None
    pending_since = 0.0
    submission = None
    blocked_data = None
    recovery_dir = backup_dir / '版本保护'
    preserve_file(recovery_dir, '最新游戏存档', baseline_bytes)
    try:
        while running:
            try:
                if connection is None:
                    connection = Connection(port, token, crc)
                    log('mGBA 已连接，正在等待 PKHeX 保存。')
                data = save_path.read_bytes()
                if submission is not None:
                    completed = connection.status(submission)
                    if completed is not None:
                        baseline, baseline_bytes = submission.updated, submission.data
                        submission = None
                        pending_data = None
                        log('事务已确认完成，已恢复提交回执。')
                        continue
                    if data != submission.data:
                        preserve_file(recovery_dir, '未提交草稿', submission.data)
                        submission = None
                    else:
                        snapshot = connection.snapshot()
                        patches = plan(baseline, submission.updated, snapshot)
                        connection.apply(snapshot, patches, submission)
                        baseline, baseline_bytes = submission.updated, submission.data
                        submission = None
                        pending_data = None
                        log('已即时同步，事务提交已确认。')
                        continue
                if data == blocked_data:
                    time.sleep(0.5)
                    continue
                if data == baseline_bytes:
                    pending_data = None
                    time.sleep(0.3)
                    continue
                if data != pending_data:
                    pending_data, pending_since = data, time.monotonic()
                if time.monotonic() - pending_since < 0.3:
                    time.sleep(0.1)
                    continue
                updated = Save.parse(data, require_latest_complete=True)
                # The game rotates save sectors and increments this counter. PKHeX
                # preserves it, so ordinary in-game saves only advance our baseline.
                difference = (updated.counter - baseline.counter) & 0xFFFFFFFF
                if 0 < difference < 0x80000000:
                    latest = preserve_file(recovery_dir, '最新游戏存档', data)
                    baseline, baseline_bytes = updated, data
                    pending_data = None
                    log(f'游戏保存版本已更新。编辑窗口刷新前的旧版本将被阻止。最新游戏存档 {latest}')
                    continue
                if updated.counter != baseline.counter:
                    draft = preserve_file(recovery_dir, '过期编辑草稿', data)
                    blocked_data = data
                    validate_edit_version(baseline, updated)
                snapshot = connection.snapshot()
                patches = plan(baseline, updated, snapshot)
                if save_path.read_bytes() != data:
                    continue
                if patches:
                    submission = PendingEdit(data, updated, snapshot.session, snapshot.epoch)
                    response = connection.apply(snapshot, patches, submission)
                    submission = None
                    log(f'已即时同步 {len(patches)} 处数据，{response.split()[-1]} 帧。重新打开当前游戏菜单可刷新显示。')
                else:
                    log('存档与运行数据已一致。')
                baseline, baseline_bytes = updated, data
                pending_data = None
                last_message = None
            except StaleTransactionError as error:
                if submission is not None:
                    preserve_file(recovery_dir, '时间线变化草稿', submission.data)
                    blocked_data = submission.data
                    submission = None
                log(str(error))
            except (OSError, ConnectionError) as error:
                if connection is not None:
                    connection.close()
                    connection = None
                message = '等待 mGBA 连接脚本。' if isinstance(error, ConnectionRefusedError) else f'连接等待 {error}'
                if message != last_message:
                    log(message)
                    last_message = message
                time.sleep(0.5)
            except (BridgeError, ValueError) as error:
                message = str(error)
                if message != last_message:
                    log(message)
                    last_message = message
                time.sleep(0.5)
    finally:
        try:
            if submission is not None:
                preserve_file(recovery_dir, '待确认事务草稿', submission.data)
        finally:
            if connection is not None:
                connection.close()
            shared_lock.close()
            lock.close()
        log('桥接程序已结束。')


def stop(_signal, _frame):
    global running
    running = False


def main():
    parser = argparse.ArgumentParser(description='PKHeX → mGBA 绿宝石实时桥接')
    parser.add_argument('--rom', type=Path, default=DEFAULT_ROM)
    parser.add_argument('--save', type=Path)
    parser.add_argument('--no-launch', action='store_true')
    args = parser.parse_args()
    signal.signal(signal.SIGINT, stop)
    signal.signal(signal.SIGTERM, stop)
    try:
        serve(args)
    except (BridgeError, OSError) as error:
        log(str(error))
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())

"""Regression checks for live encryption changes and concurrent game progress."""

import importlib.util
from pathlib import Path
import struct
import os
import sys
import unittest
import dataclasses
import tempfile
import threading
import time
from unittest import mock

spec = importlib.util.spec_from_file_location('gba_bridge', Path(__file__).with_name('bridge.py'))
bridge = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = bridge
spec.loader.exec_module(bridge)

SOURCE = Path(os.environ.get('GBA_TEST_SAVE', str(bridge.DEFAULT_ROM.with_suffix('.sav'))))


def build_synthetic_save() -> bytes:
    small = bytearray(0xF2C)
    small[:8] = bytes.fromhex('0E2C084CFF000000')
    small[10:14] = bytes.fromhex('965BFC2D')
    large = bytearray(0x3D88)
    large[0x234] = 4
    for slot, pid in enumerate((0x5587BF16, 0x656ED628, 0xDFFD7B88, 0x91FD6448)):
        mon = bytearray(100)
        struct.pack_into('<II', mon, 0, pid, 0x2DFC5B96)
        mon[8:18] = bytes.fromhex('09660550FF0000000000')
        mon[18] = 2
        mon[19] = 2
        mon[20:27] = bytes.fromhex('0E2C084CFF0000')
        struct.pack_into('<H', mon, 32, 151)
        struct.pack_into('<I', mon, 36, 1000000)
        struct.pack_into('<HHHH', mon, 44, 85, 100, 101, 102)
        mon[52:56] = bytes((10, 15, 20, 25))
        mon[84] = 100
        struct.pack_into('<HHHHHHH', mon, 86, 500, 500, 300, 300, 300, 300, 300)
        large[0x238 + slot * 100:0x238 + (slot + 1) * 100] = bridge.crypt_mon(mon)
    struct.pack_into('<I', large, 0x490, 3000)
    struct.pack_into('<HH', large, 0x650, 1, 99)
    boxes = bytearray(0x83D0)
    encoded = bridge.encode_with_key(bridge.Save(bytes(small), bytes(large), bytes(boxes), 1), 0x12345678)
    return encode_fixture(dataclasses.replace(encoded, counter=1), b'\0' * 0x20010)


def load_test_save_bytes() -> bytes:
    return SOURCE.read_bytes() if SOURCE.is_file() else build_synthetic_save()


class MergeTests(unittest.TestCase):
    def setUp(self):
        self.source_bytes = load_test_save_bytes()
        self.saved = bridge.Save.parse(self.source_bytes)
        self.plain = bridge.canonical(self.saved)

    def snapshot(self, plain):
        raw = bridge.encode_with_key(plain, 0x89ABCDEF)
        return bridge.Snapshot(5000, 0x02025000, 0x02035000, 0x02029000, raw,
                               bytes([plain.large[0x234]]) + plain.large[0x238:0x490])

    def materialize(self, snapshot, patches):
        regions = [(snapshot.small_address, bytearray(snapshot.raw.small)),
                   (snapshot.large_address, bytearray(snapshot.raw.large)),
                   (snapshot.box_address, bytearray(snapshot.raw.boxes)),
                   (bridge.PARTY_ADDRESS, bytearray(snapshot.party[1:])),
                   (bridge.PARTY_COUNT, bytearray(snapshot.party[:1]))]
        for address, before, after in patches:
            match = next((region for region in regions if region[0] <= address < region[0] + len(region[1])), None)
            self.assertIsNotNone(match)
            start, data = match
            offset = address - start
            self.assertEqual(bytes(data[offset:offset + len(before)]), before)
            data[offset:offset + len(after)] = after
        return bridge.Save(bytes(regions[0][1]), bytes(regions[1][1]), bytes(regions[2][1])), bytes(regions[3][1])

    def live(self, plain):
        return self.snapshot(plain)

    def test_encryption_roundtrip(self):
        for key in (0, 1, 0xFFFFFFFF, 0x89ABCDEF):
            self.assertEqual(bridge.canonical(bridge.encode_with_key(self.plain, key)), self.plain)

    def test_bag_edit_keeps_live_key_location_and_hp(self):
        new_large = bytearray(self.plain.large)
        struct.pack_into('<H', new_large, 0x652, 88)
        updated = bridge.encode_with_key(bridge.Save(self.plain.small, bytes(new_large), self.plain.boxes), 1234)
        live_large = bytearray(self.plain.large)
        live_large[:4] = b'\x22\x00\x33\x00'
        struct.pack_into('<H', live_large, 0x238 + 100 + 86, 456)
        live = self.live(bridge.Save(self.plain.small, bytes(live_large), self.plain.boxes))
        raw, party = self.materialize(live, bridge.plan(self.saved, updated, live))
        result = bridge.canonical(raw)
        self.assertEqual(struct.unpack_from('<H', result.large, 0x652)[0], 88)
        self.assertEqual(struct.unpack_from('<I', raw.small, 0xAC)[0], 0x89ABCDEF)
        self.assertEqual(result.large[:4], b'\x22\x00\x33\x00')
        self.assertEqual(struct.unpack_from('<H', party, 100 + 86)[0], 456)

    def test_pokemon_move_edit_keeps_live_hp_and_unedited_experience(self):
        offset = 0x238 + 100
        old = self.plain.large[offset:offset + 100]
        edited = bridge.uncrypt_mon(old)
        struct.pack_into('<H', edited, 44, 94)
        edited[52] = 10
        new_large = bytearray(self.plain.large)
        new_large[offset:offset + 100] = bridge.crypt_mon(edited)
        live_mon = bridge.uncrypt_mon(old)
        struct.pack_into('<H', live_mon, 86, 456)
        live_large = bytearray(self.plain.large)
        live_large[offset:offset + 100] = bridge.crypt_mon(live_mon)
        live = self.live(bridge.Save(self.plain.small, bytes(live_large), self.plain.boxes))
        updated = bridge.encode_with_key(bridge.Save(self.plain.small, bytes(new_large), self.plain.boxes), 1234)
        _, party = self.materialize(live, bridge.plan(self.saved, updated, live))
        result = bridge.uncrypt_mon(party[100:200])
        self.assertEqual(struct.unpack_from('<H', result, 44)[0], 94)
        self.assertEqual(result[52], 10)
        self.assertEqual(struct.unpack_from('<H', result, 86)[0], 456)
        self.assertEqual(result[36:40], live_mon[36:40])

    def test_unrelated_event_bits_survive(self):
        new_large = bytearray(self.plain.large)
        new_large[0x1280] ^= 1
        live_large = bytearray(self.plain.large)
        live_large[0x1280] ^= 2
        live = self.live(bridge.Save(self.plain.small, bytes(live_large), self.plain.boxes))
        updated = bridge.encode_with_key(bridge.Save(self.plain.small, bytes(new_large), self.plain.boxes), 1234)
        result, _ = self.materialize(live, bridge.plan(self.saved, updated, live))
        self.assertEqual(bridge.canonical(result).large[0x1280], self.plain.large[0x1280] ^ 3)

    def test_replaced_live_pokemon_is_rejected(self):
        old = self.plain.large[0x238 + 100:0x238 + 200]
        edited = bridge.uncrypt_mon(old)
        struct.pack_into('<H', edited, 44, 94)
        live = bytearray(old)
        live[0] ^= 1
        with self.assertRaises(bridge.BridgeError):
            bridge.merge_mon(old, bridge.crypt_mon(edited), bytes(live))

    def test_party_swap_carries_live_hp(self):
        before = self.plain.large[0x238:0x490]
        after = before[100:200] + before[:100] + before[200:]
        current = bytearray(before)
        struct.pack_into('<H', current, 186, 456)
        merged = bridge.merge_slots(before, after, bytes(current), 100)
        self.assertEqual(merged[:8], before[100:108])
        self.assertEqual(struct.unpack_from('<H', merged, 86)[0], 456)
        self.assertEqual(merged[100:108], before[:8])

    def test_incomplete_newer_save_is_deferred(self):
        data = bytearray(self.source_bytes)
        counters = [struct.unpack_from('<I', data, i + 0xFFC)[0] for i in range(0, 28 * 4096, 4096)
                    if struct.unpack_from('<I', data, i + 0xFF8)[0] == 0x08012025]
        # Introduce just one sector of a newer slot, as happens during a save.
        for offset in range(0, 28 * 4096, 4096):
            if struct.unpack_from('<I', data, offset + 0xFF8)[0] != 0x08012025:
                struct.pack_into('<HHII', data, offset + 0xFF4, 0, 0, 0x08012025, max(counters) + 1)
                break
        else:
            self.skipTest('This fixture already contains two populated save slots')
        with self.assertRaises(bridge.BridgeError):
            bridge.Save.parse(bytes(data), require_latest_complete=True)


class ConsistencyTests(unittest.TestCase):
    setUp = MergeTests.setUp
    snapshot = MergeTests.snapshot
    def test_unknown_field_crossing_chunk_boundary_is_fully_guarded(self):
        before = b'\0' * 600
        after = bytearray(before)
        after[255] = 1
        patches = list(bridge.changed_spans(before, bytes(after), 0x02000000))
        self.assertEqual(sum(len(p[1]) for p in patches), 600)
        self.assertEqual(patches[1][0], 0x02000100)

    def test_money_patch_guards_all_four_bytes(self):
        base_large = bytearray(self.plain.large)
        struct.pack_into('<I', base_large, 0x490, 1000)
        original = bridge.Save(self.plain.small, bytes(base_large), self.plain.boxes)
        target_large = bytearray(base_large)
        struct.pack_into('<I', target_large, 0x490, 1001)
        target = bridge.Save(self.plain.small, bytes(target_large), self.plain.boxes)
        snapshot = self.snapshot(original)
        patches = bridge.plan(original, target, snapshot)
        patch = next(p for p in patches if p[0] == snapshot.large_address + 0x490)
        self.assertEqual(len(patch[1]), 4)
        game = bytearray(snapshot.raw.large)
        struct.pack_into('<I', game, 0x490, 744 ^ 0x89ABCDEF)
        self.assertNotEqual(game[0x490:0x494], patch[1])
        self.assertEqual(struct.unpack_from('<I', game, 0x490)[0] ^ 0x89ABCDEF, 744)

    def test_changed_mon_is_guarded_as_complete_record(self):
        after = bytearray(self.plain.large)
        mon = bridge.uncrypt_mon(after[0x29C:0x300])
        struct.pack_into('<H', mon, 44, 94)
        after[0x29C:0x300] = bridge.crypt_mon(mon)
        snap = self.snapshot(self.plain)
        patches = bridge.plan(self.plain, bridge.Save(self.plain.small, bytes(after), self.plain.boxes), snap)
        record = next(p for p in patches if p[0] == bridge.PARTY_ADDRESS + 100)
        self.assertEqual(len(record[1]), 100)
        self.assertIn((snap.small_address + 0xAC, snap.raw.small[0xAC:0xB0], snap.raw.small[0xAC:0xB0]), patches)

    def test_older_counter_rejected_and_wraparound_supported(self):
        with self.assertRaises(bridge.BridgeError):
            bridge.validate_edit_version(dataclasses.replace(self.saved, counter=2), dataclasses.replace(self.saved, counter=1))
        with self.assertRaises(bridge.BridgeError):
            bridge.validate_edit_version(dataclasses.replace(self.saved, counter=0), dataclasses.replace(self.saved, counter=0xFFFFFFFF))
        bridge.validate_edit_version(self.saved, self.saved)

    def test_applied_receipt_skips_resubmit_after_game_spend(self):
        pending = bridge.PendingEdit(b'edited', self.saved, 'session', 3)
        connection = bridge.Connection.__new__(bridge.Connection)
        sent = []
        connection.send = sent.append
        connection.line = lambda: f'APPLIED {pending.identity} 1 123'
        result = connection.apply(self.snapshot(self.plain), [], pending)
        self.assertIn('APPLIED', result)
        self.assertEqual(sent, [f'STATUS {pending.identity} session 3'])

    def test_stale_timeline_rejected_before_any_write(self):
        pending = bridge.PendingEdit(b'edited', self.saved, 'session', 3)
        connection = bridge.Connection.__new__(bridge.Connection)
        sent = []
        connection.send = sent.append
        connection.line = lambda: f'STALE {pending.identity}'
        with self.assertRaises(bridge.StaleTransactionError):
            connection.apply(self.snapshot(self.plain), [], pending)
        self.assertEqual(len(sent), 1)

    def test_unknown_transaction_reuses_logical_identity(self):
        pending = bridge.PendingEdit(b'edited', self.saved, 'session', 3)
        connection = bridge.Connection.__new__(bridge.Connection)
        sent = []
        connection.send = sent.append
        replies = iter([f'UNKNOWN {pending.identity}', f'APPLIED {pending.identity} 1 123'])
        connection.line = lambda: next(replies)
        snap = dataclasses.replace(self.snapshot(self.plain), session='session', epoch=3)
        connection.apply(snap, [(0x02025000, b'a', b'b')], pending)
        self.assertIn(f'BEGIN {pending.identity} session 3', sent[1])
        self.assertIn(f'COMMIT {pending.identity}', sent[1])


def encode_fixture(value, source):
    data = bytearray(source)
    payloads = [value.small]
    offset = 0
    for size in bridge.SIZES[1:5]:
        payloads.append(value.large[offset:offset + size])
        offset += size
    offset = 0
    for size in bridge.SIZES[5:]:
        payloads.append(value.boxes[offset:offset + size])
        offset += size
    for index, payload in enumerate(payloads):
        start = index * 4096
        data[start:start + 4096] = b'\0' * 4096
        data[start:start + len(payload)] = payload
        struct.pack_into('<HHII', data, start + 0xFF4, index, bridge.checksum(payload), 0x08012025, value.counter)
    return bytes(data)


class WatcherTests(unittest.TestCase):
    setUp = MergeTests.setUp
    def test_old_editor_after_game_save_is_archived_and_blocked(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            rom = root / 'test.gba'
            rom.write_bytes(b'\0' * 0xAC + b'BPEE')
            path = root / 'test.sav'
            source = self.source_bytes
            base = dataclasses.replace(self.saved, counter=100)
            path.write_bytes(encode_fixture(base, source))
            progress = bytearray(base.large)
            progress[:4] = struct.pack('<HH', 9, 9)
            progress[0x1280] |= 4
            newer = dataclasses.replace(base, large=bytes(progress), counter=101)
            old_edit = bytearray(base.large)
            old_edit[0x652] ^= 1
            old_draft = encode_fixture(dataclasses.replace(base, large=bytes(old_edit)), source)
            messages = []
            fake = mock.Mock()
            args = type('Args', (), {'rom':rom, 'save':path, 'no_launch':True})()
            def wait_for(predicate):
                deadline = time.monotonic() + 4
                while not predicate() and time.monotonic() < deadline:
                    time.sleep(0.02)
                self.assertTrue(predicate(), messages)
            with mock.patch.object(bridge,'ROOT',root), mock.patch.object(bridge,'Connection',return_value=fake), mock.patch.object(bridge,'log',messages.append), mock.patch.object(bridge.Path,'home',return_value=root):
                bridge.running = True
                thread = threading.Thread(target=bridge.serve,args=(args,))
                thread.start()
                try:
                    wait_for(lambda: any('已连接' in m for m in messages))
                    path.write_bytes(encode_fixture(newer,source))
                    wait_for(lambda: any('版本已更新' in m for m in messages))
                    path.write_bytes(old_draft)
                    wait_for(lambda: any('过期' in m for m in messages))
                    fake.apply.assert_not_called()
                    fake.snapshot.assert_not_called()
                    copies = list((root/'存档备份/版本保护').glob('最新游戏存档_*.sav'))
                    latest = next(bridge.Save.parse(p.read_bytes()) for p in copies if bridge.Save.parse(p.read_bytes()).counter == 101)
                    self.assertEqual(latest.large[:4],struct.pack('<HH',9,9))
                    self.assertTrue(latest.large[0x1280] & 4)
                    self.assertEqual(next((root/'存档备份/版本保护').glob('过期编辑草稿_*.sav')).read_bytes(),old_draft)
                finally:
                    bridge.running = False
                    thread.join(3)
                self.assertFalse(thread.is_alive())


if __name__ == '__main__':
    unittest.main()

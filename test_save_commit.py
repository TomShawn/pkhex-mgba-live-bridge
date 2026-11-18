"""Coordinated disk/Flash tests against the actual Lua commit implementation."""

from pathlib import Path
import dataclasses
import tempfile
import unittest

from lupa.lua54 import LuaRuntime
from test_bridge import bridge, encode_fixture, load_test_save_bytes

ROOT = Path(__file__).resolve().parent
RESOURCES = ROOT / 'src/Resources'


class SaveCommitTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.path = Path(self.folder.name) / 'test.sav'
        self.expected = load_test_save_bytes()
        self.path.write_bytes(self.expected)
        saved = bridge.Save.parse(self.expected)
        self.newer = encode_fixture(dataclasses.replace(saved, counter=saved.counter + 1), self.expected)
        self.desired = self.expected  # RAM patch is separately guarded by the protocol.
        self.lua = LuaRuntime(encoding=None)
        self.lua.execute((ROOT / 'src/tests/lua_harness.lua').read_bytes())
        self.g = self.lua.globals()
        self.g.flash = self.expected[:0x20000]
        self.g.GBA_BRIDGE_CONFIG.save_module = str(RESOURCES / 'save_commit.lua').encode()
        self.g.GBA_BRIDGE_CONFIG.save_path = str(self.path).encode()
        self.lua.execute(b'''
            load_calls = 0
            state_load_calls = 0
            flash_bank = 0
            function emu:saveStateBuffer(flags)
                local raw = string.pack('<I4I4I4', frame, get32(0x02025490), flash_bank) .. string.rep('x', 52)
                if flags == 0 then return raw end
                return raw .. string.pack('<I4I4I8', 2, #flash, 96) .. string.rep('\\0',16) .. flash
            end
            function emu:loadSaveFile(path)
                load_calls = load_calls + 1
                if fail_load then fail_load = false return false end
                if backend then backend:close() end
                backend = assert(io.open(path,'r+b'))
                flash = backend:read(0x20000)
                flash_bank = 0
                if corrupt_load_at == load_calls then flash = string.rep('x', #flash) end
                return true
            end
            function emu:loadStateBuffer(state, flags)
                state_load_calls = state_load_calls + 1
                if fail_state_load or fail_state_load_at == state_load_calls then return false end
                frame, money, flash_bank = string.unpack('<I4I4I4', state)
                put32(0x02025490, money)
                if #state > 64 then
                    local tag, size, offset = string.unpack('<I4I4I8', state, 65)
                    assert(tag == 2 and size == 0x20000 and offset == 96)
                    flash = state:sub(offset + 1, offset + size)
                    flash_bank = 0 -- mGBA installs the mask after core deserialization.
                    -- flags=2 writes to the real mapping; flags=0 defers writeback.
                    if flags & 2 ~= 0 then flush_game(flash) end
                    if corrupt_state_restore then flash = string.rep('x', #flash) end
                end
                return true
            end
            function flush_game(data)
                flash = data:sub(1,0x20000)
                assert(backend:seek('set',0))
                assert(backend:write(data))
                assert(backend:flush())
            end
        ''')
        self.addCleanup(lambda: self.lua.execute(b'if backend then backend:close() end'))
        self.lua.execute((RESOURCES / 'mgba_bridge.lua').read_bytes())
        self.peer = self.g.connect()
        self.session = self.g.gbaLiveBridge.session
        self.epoch = self.g.gbaLiveBridge.epoch
        self.g.put32(0x02025490, 1000)

    def commit(self):
        command = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                   b'PATCH 2025490 e8030000 e9030000\nDISK %s %s\nCOMMIT abcdef')
        return self.g.request(self.peer, command % (
            self.session, self.epoch, self.expected.hex().encode(), self.desired.hex().encode()))

    def commit_live(self, flash_before, desired=None):
        command = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                   b'PATCH 2025490 e8030000 e9030000\nDISKLIVE %s %s %s\nCOMMIT abcdef')
        return self.g.request(self.peer, command % (
            self.session, self.epoch, self.expected.hex().encode(), flash_before.hex().encode(),
            (desired or self.newer).hex().encode()))

    def test_live_rename_failure_restores_distinct_flash_and_later_disk_writes(self):
        flash_before = self.newer[:0x20000]
        self.g.flash = flash_before
        self.g.flash_bank = 1
        self.lua.execute(b'os.rename = function() return nil,"injected rename failure" end')
        result = self.commit_live(flash_before, self.expected)
        self.assertIn(b'Cannot replace save image', result)
        self.assertEqual(self.path.read_bytes(), self.expected)
        self.assertEqual(self.g.flash, flash_before)
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.assertEqual(self.g.frame, 100)
        self.assertEqual(self.g.flash_bank, 1)
        self.assertEqual(self.g.state_load_calls, 2)
        self.assertFalse(list(self.path.parent.glob('*.tmp')))
        self.g.flush_game(self.newer)
        self.assertEqual(self.path.read_bytes(), self.newer)

    def test_live_post_bind_verification_failure_restores_flash(self):
        self.g.flash = self.newer[:0x20000]
        self.g.corrupt_load_at = 1
        result = self.commit_live(self.g.flash)
        self.assertIn(b'Staged Flash verification failed', result)
        self.assertEqual(self.g.flash, self.newer[:0x20000])
        self.assertEqual(self.path.read_bytes(), self.expected)
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.assertFalse(list(self.path.parent.glob('*.tmp')))

    def test_live_ram_write_failure_keeps_flash_without_loading_state(self):
        self.g.flash = self.newer[:0x20000]
        self.g.fail_write_at = 0x02025490
        self.assertIn(b'Write verification failed', self.commit_live(self.g.flash))
        self.assertEqual(self.g.flash, self.newer[:0x20000])
        self.assertEqual(self.path.read_bytes(), self.expected)
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.assertEqual(self.g.state_load_calls, 0)
        self.assertFalse(list(self.path.parent.glob('*.tmp')))

    def test_live_state_restore_failure_retains_stage_and_reports_error(self):
        for failure in ('checkpoint', 'raw', 'verification'):
            with self.subTest(failure=failure):
                self.g.flash = self.newer[:0x20000]
                self.g.state_load_calls = 0
                self.g.fail_state_load_at = {'checkpoint': 1, 'raw': 2}.get(failure)
                self.g.corrupt_state_restore = failure == 'verification'
                self.lua.execute(b'os.rename = function() return nil,"injected rename failure" end')
                result = self.commit_live(self.g.flash)
                self.assertIn(b'staged save retained', result)
                self.assertNotIn(b'APPLIED', result)
                self.assertEqual(self.path.read_bytes(), self.expected)
                self.assertTrue(list(self.path.parent.glob('*.tmp')))
                self.assertEqual(self.g.get32(0x02025490), 1000)

    def test_game_save_after_editor_validation_is_preserved(self):
        # The expected image was already read and validated in the editor.
        self.path.write_bytes(self.newer)
        self.g.flash = self.newer[:0x20000]
        self.assertIn(b'disk changed', self.commit())
        self.assertEqual(self.path.read_bytes(), self.newer)
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.assertEqual(self.g.load_calls, 0)

    def test_unflushed_new_flash_is_preserved(self):
        self.g.flash = self.newer[:0x20000]
        self.assertIn(b'Flash changed', self.commit())
        self.assertEqual(self.path.read_bytes(), self.expected)
        self.assertEqual(self.g.flash, self.newer[:0x20000])
        self.assertEqual(self.g.get32(0x02025490), 1000)

    def test_rebind_then_rename_keeps_later_game_flush_on_real_file(self):
        self.assertIn(b'APPLIED', self.commit())
        self.assertEqual(self.g.get32(0x02025490), 1001)
        self.g.flush_game(self.newer)
        self.assertEqual(self.path.read_bytes(), self.newer)
        self.assertFalse(list(self.path.parent.glob('*.tmp')))

    def test_rename_failure_restores_backend_and_ram(self):
        self.lua.execute(b'os.rename = function() return nil,"injected rename failure" end')
        self.assertIn(b'ERROR', self.commit())
        self.assertEqual(self.path.read_bytes(), self.expected)
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.g.flush_game(self.newer)
        self.assertEqual(self.path.read_bytes(), self.newer)
        self.assertFalse(list(self.path.parent.glob('*.tmp')))

    def test_backend_load_failure_keeps_original_file_and_ram(self):
        self.g.fail_load = True
        self.assertIn(b'ERROR', self.commit())
        self.assertEqual(self.path.read_bytes(), self.expected)
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.assertFalse(list(self.path.parent.glob('*.tmp')))

    def test_lost_receipt_does_not_replace_a_later_game_save(self):
        self.peer.drop_replies = True
        self.assertEqual(self.commit(), b'')
        self.g.flush_game(self.newer)
        self.g.put32(0x02025490, 1000)
        self.peer = self.g.connect()
        self.assertIn(b'APPLIED', self.commit())
        self.assertEqual(self.path.read_bytes(), self.newer)
        self.assertEqual(self.g.get32(0x02025490), 1000)

    def test_live_commit_can_guard_a_flash_timeline_different_from_disk(self):
        self.g.flash = self.newer[:0x20000]
        command = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                   b'DISKLIVE %s %s %s\nCOMMIT abcdef')
        result = self.g.request(self.peer, command % (self.session, self.epoch,
            self.expected.hex().encode(), self.g.flash.hex().encode(), self.newer.hex().encode()))
        self.assertIn(b'APPLIED', result)
        self.assertEqual(self.path.read_bytes(), self.newer)

    def test_live_flash_change_after_capture_is_rejected(self):
        self.g.flash = self.newer[:0x20000]
        command = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                   b'DISKLIVE %s %s %s\nCOMMIT abcdef')
        result = self.g.request(self.peer, command % (self.session, self.epoch,
            self.expected.hex().encode(), self.expected[:0x20000].hex().encode(), self.desired.hex().encode()))
        self.assertIn(b'Flash changed', result)
        self.assertEqual(self.path.read_bytes(), self.expected)

    def test_live_snapshot_includes_the_complete_current_flash(self):
        self.g.request(self.peer, b'SNAPLIVE')
        for _ in range(64):
            if self.peer.output.endswith(b'END\n'):
                break
            self.g.fire(b'frame')
        lines = self.peer.output.splitlines()
        flash = next(line[6:] for line in lines if line.startswith(b'FLASH '))
        self.assertEqual(bytes.fromhex(flash.decode()), self.g.flash)
        disk = next(line[10:] for line in lines if line.startswith(b'DISKBYTES '))
        self.assertEqual(bytes.fromhex(disk.decode()), self.path.read_bytes())


if __name__ == '__main__':
    unittest.main()

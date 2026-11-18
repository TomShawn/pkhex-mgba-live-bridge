"""Regression tests for the actual Lua protocol, using an isolated Lua 5.4 VM."""

from pathlib import Path
import unittest

from lupa.lua54 import LuaRuntime

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / 'src/Resources/mgba_bridge.lua'
HARNESS = ROOT / 'src/tests/lua_harness.lua'


class NativeProtocolTests(unittest.TestCase):
    def setUp(self):
        self.lua = LuaRuntime(encoding=None)
        self.lua.execute(HARNESS.read_bytes())
        self.g = self.lua.globals()
        self.g.GBA_BRIDGE_CONFIG.native = True
        self.g.put32(0x030022C0, 0x08085E05)
        self.g.socket.bind = self.lua.eval(b'function() error("Native mode must not bind TCP") end')
        self.lua.execute(SOURCE.read_bytes())
        self.bridge = self.g.gbaLiveBridge
        self.assertIn(b'HELLO', self.request(b'1', b'AUTH token'))

    def request(self, identity, commands):
        return self.bridge.nativeRequest(self.bridge, identity, commands)

    def transaction(self, identity):
        commands = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                    b'PATCH 2025490 e8030000 e9030000\nCOMMIT abcdef')
        return self.request(identity, commands % (self.bridge.session, self.bridge.epoch))

    def prepare_bag(self, key=0):
        self.g.put32(0x020350AC, key)
        self.g.put32(0x02025650, 1 | ((73 ^ key) << 16))
        self.g.put32(0x03005E00, 0x081ABD29)
        self.g.memory[0x03005E04] = 1
        self.g.put32(0x03005E28, 0x081AE459)
        self.g.memory[0x03005E2C] = 1
        self.g.put32(0x03005E34, 0x081AB451)
        self.g.put32(0x03005E38, 0x081AB521)

    def change_bag(self, count=74, item=1, key=0):
        before = (1 | ((73 ^ key) << 16)).to_bytes(4, 'little').hex().encode()
        after = (item | ((count ^ key) << 16)).to_bytes(4, 'little').hex().encode()
        command = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                   b'PATCH 2025650 %s %s\nCOMMIT abcdef')
        self.assertIn(b'APPLIED', self.request(b'1', command % (self.bridge.session, self.bridge.epoch, before, after)))

    def test_bag_quantity_uses_game_redraw_task_and_restores_idle_function(self):
        self.prepare_bag(key=0x1234)
        self.change_bag(key=0x1234)
        self.assertEqual(self.g.get32(0x03005E28), 0x081AE70D)
        self.g.frame += 1
        self.g.fire(b'frame')
        self.assertEqual(self.g.get32(0x03005E28), 0x081AE70D)
        self.g.frame += 1
        self.g.fire(b'frame')
        self.assertEqual(self.g.get32(0x03005E28), 0x081AE459)

    def test_bag_structural_change_keeps_original_list_task(self):
        for count, item in ((0, 1), (74, 2)):
            with self.subTest(count=count, item=item):
                self.setUp()
                self.prepare_bag()
                self.change_bag(count=count, item=item)
                self.assertEqual(self.g.get32(0x03005E28), 0x081AE459)

    def test_bag_action_submenu_keeps_original_list_task(self):
        self.prepare_bag()
        self.g.put32(0x03005E00, 0x081ABE01)
        self.change_bag()
        self.assertEqual(self.g.get32(0x03005E28), 0x081AE459)

    def test_bag_redraw_is_cleaned_on_timeline_change_and_script_close(self):
        for event in (b'stateLoaded', b'close'):
            with self.subTest(event=event):
                self.setUp()
                self.prepare_bag()
                self.change_bag()
                if event == b'close':
                    self.bridge.close(self.bridge)
                else:
                    self.g.fire(event)
                self.assertEqual(self.g.get32(0x03005E28), 0x081AE459)

    def test_bag_redraw_does_not_restore_over_a_reused_task(self):
        self.prepare_bag()
        self.change_bag()
        self.g.put32(0x03005E28, 0x081ABC01)
        self.g.frame += 2
        self.g.fire(b'frame')
        self.assertEqual(self.g.get32(0x03005E28), 0x081ABC01)

    def test_native_snapshot_is_complete_and_survives_frame_callbacks(self):
        self.g.fire(b'frame')
        result = self.request(b'1', b'SNAP')
        self.assertTrue(result.endswith(b'END\n'))
        self.assertGreater(len(result), 90000)
        self.assertEqual(result.count(b'BOXES '), 1)

    def test_native_authentication_and_lease_release(self):
        self.g.put32(0x02025490, 1000)
        self.assertIn(b'ERROR', self.request(b'2', b'SNAP'))
        self.assertIn(b'HELLO', self.request(b'2', b'AUTH token'))
        self.assertIn(b'APPLIED', self.transaction(b'1'))
        self.assertIn(b'Another editor', self.transaction(b'2'))
        self.assertEqual(self.request(b'1', b'DETACH'), b'DETACHED\n')
        self.assertIn(b'APPLIED', self.transaction(b'2'))

    def test_native_disconnect_lost_receipt_remains_idempotent(self):
        self.g.put32(0x02025490, 1000)
        self.transaction(b'1')
        self.request(b'1', b'DETACH')
        self.g.put32(0x02025490, 1000)
        self.request(b'2', b'AUTH token')
        self.assertIn(b'APPLIED', self.transaction(b'2'))
        self.assertEqual(self.g.get32(0x02025490), 1000)

    def test_native_editor_exit_keeps_emulator_bridge_available(self):
        self.assertIn(b'ERROR', self.request(b'1', b'STOP'))
        self.assertIn(b'PONG', self.request(b'1', b'PING'))

    def test_native_title_screen_waits_for_the_overworld(self):
        self.g.put32(0x030022C0, 0)
        self.assertTrue(self.request(b'1', b'SNAP').startswith(b'WAIT'))
        self.g.put32(0x030022C0, 0x08085E05)
        self.assertTrue(self.request(b'1', b'SNAP').endswith(b'END\n'))

    def test_native_full_snapshot_guard_preserves_unedited_progress(self):
        self.g.put32(0x02025490, 1000)
        lines = self.request(b'1', b'SNAP').splitlines()
        fields = dict(line.split(b' ', 1) for line in lines[1:-1])
        guard = b'GUARDALL ' + b' '.join(fields[key] for key in (b'SMALL', b'LARGE', b'BOXES', b'PARTY'))
        commands = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                    b'PATCH 2025490 e8030000 e9030000\n%s\nCOMMIT abcdef')
        self.g.memory[0x02025001] = 9
        reply = self.request(b'1', commands % (self.bridge.session, self.bridge.epoch, guard))
        self.assertIn(b'Snapshot changed', reply)
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.assertEqual(self.g.memory[0x02025001], 9)

    def test_foreign_trainer_starts_a_new_epoch(self):
        epoch = self.bridge.epoch
        self.g.put32(0x0203500A, 42)
        self.request(b'1', b'PING')
        self.assertGreater(self.bridge.epoch, epoch)

    def test_save_counter_advancing_before_first_flash_write_rejects_commit(self):
        self.g.put32(0x02025490, 1000)
        self.g.put32(0x03006200, 5)
        fields = dict(line.split(b' ', 1) for line in self.request(b'1', b'SNAP').splitlines()[1:-1])
        guard = b'GUARDALL ' + b' '.join(fields[key] for key in (b'SMALL', b'LARGE', b'BOXES', b'PARTY')) + b' 00000005'
        commands = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                    b'PATCH 2025490 e8030000 e9030000\n%s\nCOMMIT abcdef')
        self.g.put32(0x03006200, 6)
        result = self.request(b'1', commands % (self.bridge.session, self.bridge.epoch, guard))
        self.assertIn(b'save counter advanced', result)
        self.assertEqual(self.g.get32(0x02025490), 1000)


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        self.lua = LuaRuntime(encoding=None)
        self.lua.execute(HARNESS.read_bytes())
        self.lua.execute(SOURCE.read_bytes())
        self.g = self.lua.globals()
        self.peer = self.g.connect()
        self.session = self.g.gbaLiveBridge.session
        self.epoch = self.g.gbaLiveBridge.epoch
        self.g.put32(0x02025490, 1000)

    def apply(self, identity=b'abcdef', before=b'e8030000', after=b'e9030000'):
        return self.g.transaction(self.peer, identity, self.session, self.epoch,
                                  0x02025490, before, after)

    def query(self, identity=b'abcdef'):
        line = b'STATUS %s %s %d' % (identity, self.session, self.epoch)
        return self.g.request(self.peer, line)

    def test_remote_eof_is_released_without_a_received_event(self):
        self.peer.remote_eof = True
        self.g.fire(b'frame')
        self.assertTrue(self.peer.closed)
        self.assertEqual(self.lua.eval(b'function() local n=0 for _ in pairs(gbaLiveBridge.clients) do n=n+1 end return n end')(),0)

    def test_concurrent_744_rejects_complete_field(self):
        self.g.put32(0x02025490, 744)
        self.assertIn(b'ERROR', self.apply())
        self.assertEqual(self.g.get32(0x02025490), 744)
        self.assertIn(b'UNKNOWN', self.query())

    def test_lost_reply_reconnect_and_duplicate_commit_keep_spend(self):
        self.peer.drop_replies = True
        self.assertEqual(self.apply(), b'')
        self.assertEqual(self.g.get32(0x02025490), 1001)
        self.g.put32(0x02025490, 1000)
        self.peer = self.g.connect()
        self.assertIn(b'APPLIED abcdef', self.query())
        self.assertIn(b'APPLIED abcdef', self.apply())
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.assertEqual(self.g.gbaLiveBridge.result_count, 1)

    def test_reset_invalidates_old_result_and_pending_transaction(self):
        self.apply()
        self.g.fire(b'reset')
        self.assertIn(b'STALE', self.query())
        self.assertIn(b'STALE', self.apply())

    def test_state_load_invalidates_old_result(self):
        self.apply()
        self.g.fire(b'stateLoaded')
        self.assertIn(b'STALE', self.query())

    def test_backward_frame_invalidates_old_result(self):
        self.apply()
        self.g.frame = 50
        self.assertIn(b'STALE', self.query())

    def test_upgrade_from_legacy_bridge_initializes_session(self):
        self.g.gbaLiveBridge.generation = None
        self.lua.execute(SOURCE.read_bytes())
        self.assertTrue(self.g.gbaLiveBridge.session)
        self.assertEqual(self.g.gbaLiveBridge.generation, 1)

    def test_script_reload_changes_session(self):
        self.apply()
        self.lua.execute(SOURCE.read_bytes())
        self.peer = self.g.connect()
        self.assertNotEqual(self.session, self.g.gbaLiveBridge.session)
        self.assertIn(b'STALE', self.query())

    def test_capacity_refuses_new_transactions_without_eviction(self):
        self.apply()
        self.g.gbaLiveBridge.result_count = 4096
        self.assertIn(b'ERROR', self.apply(b'123456', b'e9030000', b'ea030000'))
        self.assertEqual(self.g.get32(0x02025490), 1001)
        self.assertIn(b'APPLIED', self.query())

    def test_failed_write_rolls_back_and_has_no_result(self):
        self.g.fail_write_at = 0x02025490
        self.assertIn(b'ERROR', self.apply())
        self.assertEqual(self.g.get32(0x02025490), 1000)
        self.assertIn(b'UNKNOWN', self.query())

    def test_all_preconditions_checked_before_first_write(self):
        command = (b'BEGIN abcdef %s %d 2025000 2035000 2029000\n'
                   b'PATCH 2025490 e8030000 e9030000\n'
                   b'PATCH 20350ac 01000000 01000000\nCOMMIT abcdef')
        result = self.g.request(self.peer, command % (self.session, self.epoch))
        self.assertIn(b'ERROR', result)
        self.assertEqual(self.g.get32(0x02025490), 1000)


if __name__ == '__main__':
    unittest.main()

"""Execute the emitted arm64 close function and verify descriptor reuse safety."""

from pathlib import Path
import importlib.util
import shutil
import struct
import tempfile
import unittest

from unicorn import Uc, UC_ARCH_ARM64, UC_MODE_ARM, UC_HOOK_CODE
from unicorn.arm64_const import UC_ARM64_REG_X0, UC_ARM64_REG_W0, UC_ARM64_REG_LR, UC_ARM64_REG_PC, UC_ARM64_REG_X20

ROOT = Path(__file__).resolve().parent / 'src'
SPEC = importlib.util.spec_from_file_location('patch_mgba', ROOT / 'tools/MgbaSocketFix/patch_mgba.py')
PATCH = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PATCH)


class NativeSocketTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        source = ROOT / 'vendor/mGBA-0.10.5-base.app/Contents/MacOS/mGBA'
        if not source.is_file():
            raise unittest.SkipTest('Requires vendor/mGBA-0.10.5-base.app binary for patch verification')
        cls.folder = tempfile.TemporaryDirectory()
        cls.binary = Path(cls.folder.name) / 'mGBA'
        shutil.copy2(source, cls.binary)
        cls.original = cls.binary.read_bytes()
        cls.info = PATCH.patch_binary(cls.binary)
        cls.patched = cls.binary.read_bytes()

    @classmethod
    def tearDownClass(cls):
        cls.folder.cleanup()

    def setUp(self):
        self.cpu = Uc(UC_ARCH_ARM64, UC_MODE_ARM)
        self.entry = int(self.info['address'], 16)
        self.cave = int(self.info['padding_address'], 16)
        self.close = int(self.info['close_target'], 16)
        self.binding = int(self.info['binding_address'],16)
        self.object = 0x200000000
        self.exit = 0x300000000
        for address in {self.entry & ~0xFFF, self.cave & ~0xFFF, self.close & ~0xFFF, self.object,
                        *[int(site['address'],16) & ~0xFFF for site in self.info['receive_checks']]}:
            self.cpu.mem_map(address, 4096)
        self.cpu.mem_write(self.entry, bytes.fromhex(self.info['replacement']))
        self.cpu.mem_write(self.cave, bytes.fromhex(self.info['fixed_leaf']))
        self.cpu.mem_write(self.close, bytes.fromhex('c0035fd6'))
        self.cpu.mem_write(self.binding,bytes.fromhex(self.info['binding_replacement']))
        self.descriptors = {}
        self.calls = []
        self.cpu.hook_add(UC_HOOK_CODE, self.on_instruction)

    def on_instruction(self, cpu, address, size, context):
        if address == self.close:
            descriptor = cpu.reg_read(UC_ARM64_REG_W0)
            self.assertEqual(bytes(cpu.mem_read(self.object, 4)), b'\xff' * 4)
            self.calls.append(descriptor)
            self.descriptors.pop(descriptor, None)
            cpu.reg_write(UC_ARM64_REG_PC, cpu.reg_read(UC_ARM64_REG_LR))

    def run_close(self):
        self.cpu.reg_write(UC_ARM64_REG_X0, self.object)
        self.cpu.reg_write(UC_ARM64_REG_LR, self.exit)
        self.cpu.emu_start(self.entry, self.exit, count=30)

    def test_explicit_close_then_destructor_preserves_reused_descriptor(self):
        self.cpu.mem_write(self.object, struct.pack('<i', 91))
        self.descriptors[91] = 'socket'
        self.run_close()
        self.assertNotIn(91, self.descriptors)
        self.descriptors[91] = 'new listener'
        self.run_close()
        self.assertEqual(self.descriptors[91], 'new listener')
        self.assertEqual(self.calls, [91])

    def test_script_binding_and_destructor_use_fixed_close(self):
        self.cpu.mem_write(self.object,struct.pack('<i',92))
        self.descriptors[92] = 'socket'
        for iteration in range(2):
            self.cpu.reg_write(UC_ARM64_REG_X20,self.object)
            self.cpu.emu_start(self.binding,self.binding + 12,count=30)
            if iteration == 0:
                self.descriptors[92] = 'reused file'
        self.assertEqual(self.descriptors[92],'reused file')
        self.assertEqual(self.calls,[92])

    def test_already_invalid_socket_remains_noop(self):
        self.cpu.mem_write(self.object, struct.pack('<i', -1))
        self.run_close()
        self.assertEqual(self.calls, [])

    def test_eof_uses_success_path_and_negative_recv_keeps_error_path(self):
        for site in self.info['receive_checks']:
            address = int(site['address'],16)
            word = site['replacement']
            delta = (word >> 5) & 0x7FFFF
            if delta & 0x40000:
                delta -= 0x80000
            error = address + delta * 4
            success = address + 4
            self.cpu.mem_write(address - 4,struct.pack('<II',0xF100001F,word))
            self.cpu.mem_write(error,struct.pack('<I',PATCH.NOP))
            self.cpu.mem_write(success,struct.pack('<I',PATCH.NOP))
            for received, expected in [(0,'success'),(7,'success'),((1 << 64)-1,'error')]:
                result = []
                def stop(cpu, pc, size, context):
                    if pc in (error,success):
                        result.append('error' if pc == error else 'success')
                        cpu.emu_stop()
                hook = self.cpu.hook_add(UC_HOOK_CODE,stop)
                self.cpu.reg_write(UC_ARM64_REG_X0,received)
                self.cpu.emu_start(address - 4,self.exit,count=10)
                self.cpu.hook_del(hook)
                self.assertEqual(result,[expected])

    def test_patch_changes_only_checked_function_and_unused_padding(self):
        start = self.info['file_offset']
        padding = self.info['padding_offset']
        self.assertEqual(len(self.original), len(self.patched))
        allowed = set(range(start, start + 16)) | set(range(padding, padding + 24))
        binding = self.info['binding_offset']
        allowed.update(range(binding,binding + 12))
        for site in self.info['receive_checks']:
            allowed.update(range(site['file_offset'],site['file_offset']+4))
        self.assertTrue(all(a == b or index in allowed
                            for index, (a, b) in enumerate(zip(self.original, self.patched))))

    def test_reapplying_to_unexpected_implementation_is_rejected(self):
        with self.assertRaisesRegex(ValueError, 'implementation differs'):
            PATCH.patch_binary(self.binary)


if __name__ == '__main__':
    unittest.main()

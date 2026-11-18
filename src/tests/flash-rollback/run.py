"""Exercise the real Lua save rollback on an already built mGBA library.

python3 run.py --native-build /path/to/source-build/native
All save writes use a temporary copy. No GUI or active emulator is required.
"""
import argparse
from pathlib import Path
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--native-build', required=True, type=Path)
    parser.add_argument('--rom', type=Path, default=ROOT / '.test-runtime/native/test.gba')
    parser.add_argument('--save', type=Path, default=ROOT / '.test-runtime/native/test.sav')
    parser.add_argument('--module', type=Path, default=ROOT / 'Resources/save_commit.lua')
    args = parser.parse_args()
    native = args.native_build.resolve()
    with tempfile.TemporaryDirectory(prefix='pkhex-flash-rollback-') as temporary:
        temporary = Path(temporary)
        executable = temporary / 'check'
        subprocess.run([
            'cc', '-include', str(native / 'include/mgba/flags.h'),
            '-I', str(ROOT / 'upstream/mgba/include'), '-I', str(native / 'include'),
            str(HERE / 'native-flash.c'), str(native / 'libmgba.0.10.5.dylib'),
            '-Wl,-rpath,' + str(native), '-o', str(executable),
        ], check=True)
        save = temporary / 'test.sav'
        shutil.copy2(args.save, save)
        subprocess.run([
            str(executable), str(args.rom.resolve()), str(save),
            str(args.module.resolve()), str(HERE / 'native-flash.lua'),
        ], check=True)


if __name__ == '__main__':
    main()

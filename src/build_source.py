"""Build the source-based editor and its embedded native-bridge emulator."""

import argparse
import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import struct
import uuid

from build_integrated import find_dotnet

ROOT = Path(__file__).resolve().parent
MACHO = {b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe", b"\xfe\xed\xfa\xcf"}


def run(command, log=None):
    if log is None:
        subprocess.run([str(value) for value in command], check=True,cwd="/tmp")
        return
    with log.open("w") as output:
        subprocess.run([str(value) for value in command], stdout=output, stderr=subprocess.STDOUT, check=True,cwd="/tmp")


def extract_bundle(executable, directory):
    data = executable.read_bytes()
    marker = bytes.fromhex("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE")
    position = struct.unpack_from("<Q",data,data.index(marker) - 8)[0]
    major, minor, count = struct.unpack_from("<III",data,position)
    if major != 6:
        raise RuntimeError(f"Unsupported .NET bundle format {major}.{minor}")
    position += 12

    def string():
        nonlocal position
        size = 0
        shift = 0
        while True:
            value = data[position]
            position += 1
            size |= (value & 127) << shift
            if value < 128:
                break
            shift += 7
        value = data[position:position + size].decode("utf-8")
        position += size
        return value

    string()
    position += 40
    directory.mkdir()
    extracted = {}
    for _ in range(count):
        offset, size, compressed, kind = struct.unpack_from("<QQQB",data,position)
        position += 25
        name = string()
        if name not in ("PKHeX.Core.dll", "PKHeX.ChineseCompat.dll"):
            continue
        if compressed:
            raise RuntimeError("Chinese verification requires an uncompressed bundle entry")
        payload = data[offset:offset + size]
        (directory / name).write_bytes(payload)
        extracted[name] = hashlib.sha256(payload).hexdigest()
    if len(extracted) != 2 or b"PKHeX.ChineseCompat" not in (directory / "PKHeX.Core.dll").read_bytes():
        raise RuntimeError("Published Core does not contain the Chinese compatibility integration")
    return extracted


def prepare(module_path, destination):
    spec = importlib.util.spec_from_file_location(destination.name, module_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.prepare(destination)


def binaries(app):
    for path in app.rglob("*"):
        if path.is_file() and not path.is_symlink():
            with path.open("rb") as source:
                if source.read(4) in MACHO:
                    yield path


def bundle_libraries(app):
    frameworks = app / "Contents/Frameworks"
    frameworks.mkdir(exist_ok=True)
    pending = list(binaries(app))
    seen = set()
    while pending:
        binary = pending.pop()
        if binary in seen:
            continue
        seen.add(binary)
        identities = subprocess.check_output(["otool", "-D", str(binary)], text=True).splitlines()[1:]
        if identities and identities[0].strip().startswith(("/opt/homebrew/", "/usr/local/")):
            run(["install_name_tool", "-id", "@rpath/" + binary.name, binary])
        output = subprocess.check_output(["otool", "-L", str(binary)], text=True)
        for index, line in enumerate(output.splitlines()[1:]):
            if index == 0 and identities:
                continue  # LC_ID_DYLIB names the current image.
            dependency = line.strip().split(" (", 1)[0]
            if dependency.startswith("@rpath/"):
                suffix = dependency.removeprefix("@rpath/")
                bundled = frameworks / suffix
                if bundled.is_file():
                    relative = os.path.relpath(bundled,binary.parent)
                    run(["install_name_tool", "-change", dependency, "@loader_path/" + relative, binary])
                    continue
                candidates = [path.resolve() for path in Path("/opt/homebrew/opt").glob("*/lib/" + suffix) if path.is_file()]
                candidates = list(dict.fromkeys(candidates))
                if len(candidates) != 1:
                    raise RuntimeError(f"Cannot resolve a unique bundled dependency for {dependency} in {binary.name}")
                source = candidates[0]
            elif dependency.startswith(("/opt/homebrew/", "/usr/local/")):
                source = Path(dependency)
            else:
                continue
            if ".framework" in str(source):
                framework = next(parent for parent in source.parents if parent.suffix == ".framework")
                bundled = frameworks / framework.name
                destination = bundled / source.relative_to(framework)
                if not bundled.exists():
                    shutil.copytree(framework,bundled,symlinks=True,ignore=shutil.ignore_patterns("Headers", "*.prl"))
                    for library in binaries(bundled):
                        run(["install_name_tool", "-id", "@rpath/" + str(library.relative_to(frameworks)), library])
                        pending.append(library)
            else:
                destination = frameworks / source.name
                if not destination.exists():
                    shutil.copy2(source, destination)
                    run(["install_name_tool", "-id", "@rpath/" + source.name, destination])
                    pending.append(destination)
            relative = os.path.relpath(destination, binary.parent)
            run(["install_name_tool", "-change", dependency, "@loader_path/" + relative, binary])
        loads = subprocess.check_output(["otool", "-l", str(binary)],text=True).splitlines()
        for index, line in enumerate(loads):
            if line.strip() == "cmd LC_RPATH":
                path = loads[index + 2].strip().removeprefix("path ").split(" (offset",1)[0]
                if path.startswith("/"):
                    run(["install_name_tool", "-delete_rpath", path, binary])
    for binary in binaries(app):
        output = subprocess.check_output(["otool", "-L", str(binary)], text=True)
        if "/opt/homebrew/" in output or "/usr/local/" in output:
            raise RuntimeError(f"Unbundled dependency in {binary.name}")


def sign(app):
    applications = [app] + [path for path in app.rglob("*.app") if path.is_dir()]
    executables = set()
    for bundle in applications:
        info = plistlib.loads((bundle / "Contents/Info.plist").read_bytes())
        executables.add(bundle / "Contents/MacOS" / info["CFBundleExecutable"])
    for binary in binaries(app):
        if binary not in executables:
            run(["codesign", "--force", "--sign", "-", binary])
    containers = [path for path in app.rglob("*") if path.is_dir() and path.suffix in (".app", ".framework", ".bundle")]
    for path in sorted(containers, key=lambda item: len(item.parts), reverse=True):
        run(["codesign", "--force", "--sign", "-", path])
    run(["codesign", "--force", "--sign", "-", app])
    run(["codesign", "--verify", "--strict", "--deep", app])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", type=Path)
    parser.add_argument("--qt-root", type=Path, default=Path("/opt/homebrew/opt/qt@5"))
    parser.add_argument("--lua-root", type=Path, default=Path("/opt/homebrew/opt/lua@5.4"))
    parser.add_argument("--output-root", type=Path, default=ROOT / "builds")
    args = parser.parse_args()
    dotnet = find_dotnet(args.dotnet)
    for name in ("mgba", "PKHeX-Avalonia"):
        if not (ROOT / "upstream" / f"{name}-source.json").exists():
            raise SystemExit(f"Fetch and verify {name} with tools/fetch_sources.py first.")
    stamp = datetime.datetime.now().strftime("%Y%m%d-%H%M%S") + "-source-" + uuid.uuid4().hex[:8]
    output = args.output_root.resolve() / stamp
    output.mkdir(parents=True)
    print(f"Building source preview in {output}", flush=True)
    os.environ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    os.environ["DOTNET_NOLOGO"] = "1"

    mgba = prepare(ROOT / "tools/MgbaBridge/prepare.py", output / "mgba-src")
    editor = prepare(ROOT / "tools/SourceEditor/prepare.py", output / "editor-src")
    native = output / "native"
    run(["cmake", "-S", mgba, "-B", native, "-DCMAKE_BUILD_TYPE=Release",
         "-DCMAKE_POLICY_VERSION_MINIMUM=3.5", "-DCMAKE_OSX_ARCHITECTURES=arm64",
         "-DCMAKE_OSX_DEPLOYMENT_TARGET=15.0", f"-DCMAKE_PREFIX_PATH={args.qt_root}",
         "-DBUILD_QT=ON", "-DBUILD_SDL=OFF", "-DUSE_FFMPEG=OFF", "-DUSE_SQLITE3=ON",
         "-DUSE_DISCORD_RPC=OFF", "-DBUILD_SUITE=OFF", "-DBUILD_TEST=OFF", "-DUSE_LUA=ON",
         f"-DLUA_INCLUDE_DIR={args.lua_root}/include/lua5.4", f"-DLUA_LIBRARY={args.lua_root}/lib/liblua.dylib"],
        output / "native-configure.log")
    run(["cmake", "--build", native, "-j", "8"], output / "native-build.log")
    print("Native emulator compiled", flush=True)
    run([dotnet, "build", ROOT / "tools/Patcher/Patcher.csproj", "-c", "Release", "-o", output / "patcher", "--nologo"],
        output / "patcher-build.log")
    publish = output / "publish"
    run([dotnet, "publish", editor / "PKHeX.Avalonia/PKHeX.Avalonia.csproj", "-c", "Release",
         "-r", "osx-arm64", "--self-contained", "true", "-p:PublishSingleFile=true",
         f"-p:GbaPatcher={output}/patcher/Patcher.dll", f"-p:GbaDotnet={dotnet}", "-o", publish, "--nologo"],
        output / "editor-build.log")
    app = output / "PKHeX实时版预览.app"
    shutil.move(publish / "PKHeX.Avalonia.app", app)
    symbols = output / "symbols"
    symbols.mkdir()
    for path in (app / "Contents/MacOS").glob("*.pdb"):
        shutil.move(path,symbols / path.name)
    print("Source editor compiled", flush=True)
    bundled = output / "bundle-check"
    bundled_hashes = extract_bundle(app / "Contents/MacOS/PKHeX.Avalonia",bundled)
    run([dotnet,"run","--project",ROOT / "tests/chinese/check.csproj","-c","Release",
         f"-p:ChineseAssemblyDir={bundled}","--",ROOT / "tools/ChineseCompat/ChineseMap.tsv"],output / "bundled-chinese-tests.log")
    print("Published bundle passed all 6767 Chinese character checks",flush=True)

    helpers = app / "Contents/Helpers"
    helpers.mkdir()
    emulator = helpers / "mGBA实时版.app"
    shutil.copytree(native / "qt/mGBA.app", emulator)
    frameworks = emulator / "Contents/Frameworks"
    frameworks.mkdir(exist_ok=True)
    shutil.copy2(native / "libmgba.0.10.5.dylib", frameworks / "libmgba.0.10.dylib")
    run(["install_name_tool", "-change", "@rpath/libmgba.0.10.dylib",
         "@executable_path/../Frameworks/libmgba.0.10.dylib", emulator / "Contents/MacOS/mGBA"])
    run([args.qt_root / "bin/macdeployqt", emulator, "-always-overwrite"], output / "native-deploy.log")
    bundle_libraries(emulator)
    for bundle, identity, version in ((app, "io.pkhex.avalonia.chinese.preview", "1.101.2-cn5-preview2"),
                                      (emulator, "io.pkhex.mgba.live", "0.10.5-live2")):
        resources = bundle / "Contents/Resources/GBABridge"
        resources.mkdir(parents=True, exist_ok=True)
        for name in ("mgba_bridge.lua", "save_commit.lua"):
            shutil.copy2(ROOT / "Resources" / name, resources / name)
        info = bundle / "Contents/Info.plist"
        data = plistlib.loads(info.read_bytes())
        data["CFBundleIdentifier"] = identity
        data["CFBundleDisplayName"] = "PKHeX实时版预览" if bundle == app else "mGBA实时版"
        data["CFBundleShortVersionString"] = version
        data["CFBundleVersion"] = version
        data["LSMinimumSystemVersion"] = "15.0"
        info.write_bytes(plistlib.dumps(data))
    licenses = app / "Contents/Resources/Licenses"
    licenses.mkdir()
    shutil.copy2(ROOT / "upstream/PKHeX-Avalonia/LICENSE", licenses / "PKHeX-GPL.txt")
    shutil.copy2(ROOT / "upstream/mgba/LICENSE", licenses / "mGBA-MPL.txt")
    qt_licenses = licenses / "Qt5"
    qt_licenses.mkdir()
    for path in args.qt_root.glob("LICENSE.*"):
        if path.name != "LICENSE.QT-LICENSE-AGREEMENT":
            shutil.copy2(path,qt_licenses / path.name)
    manifests = {name: json.loads((ROOT / "upstream" / f"{name}-source.json").read_text())
                 for name in ("mgba", "PKHeX-Avalonia")}
    source_files = [ROOT / name for name in ("BridgeEngine.cs", "BridgeModule.cs", "NativeDiscovery.cs", "SaveCommitCoordinator.cs", "LiveWorkspace.cs", "LiveMerge.cs", "build_source.py")]
    for folder in ("Resources", "tools/MgbaBridge", "tools/SourceEditor", "tools/ChineseCompat", "tools/Patcher"):
        source_files.extend(path for path in (ROOT / folder).rglob("*") if path.is_file()
                            and not any(part in ("bin", "obj", "__pycache__") for part in path.parts))
    metadata = {"app": str(app), "upstream": manifests, "phase": "live workspace preview",
                "bundled_assemblies": bundled_hashes, "bundled_chinese_verified": True,
                "dotnet": subprocess.check_output([str(dotnet), "--version"], text=True).strip(),
                "source_hashes": {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in source_files}}
    (app / "Contents/Resources/GBABridge/build-info.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2))
    sign(app)
    metadata["signature_verified"] = True
    (output / "build-info.json").write_text(json.dumps(metadata, ensure_ascii=False, indent=2))
    print(f"Built and signed {app}", flush=True)


if __name__ == "__main__":
    main()

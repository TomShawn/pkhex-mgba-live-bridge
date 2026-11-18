# Source Build & Verification Guide / 内置桥接构建与验证

**[English](#english) | [中文说明](#中文说明)**

---

<a id="english"></a>

## English

Current release target: `1.101.2-cn5-preview` (Protocol v3).

### 1. Building the Source Live Preview Edition (`build_source.py`)

[`build_source.py`](./build_source.py) compiles both the editor and the embedded `mGBA` from verified upstream sources. First-party integration code resides in [`tools/SourceEditor`](./tools/SourceEditor), [`tools/MgbaBridge`](./tools/MgbaBridge), [`LiveWorkspace.cs`](./LiveWorkspace.cs), and [`LiveMerge.cs`](./LiveMerge.cs).

#### Step 1: Fetch and Verify Upstream Sources
```bash
python3 src/tools/fetch_sources.py PKHeX-Avalonia
python3 src/tools/fetch_sources.py mgba
```

#### Step 2: Run the Source Build
```bash
cd /tmp
python3 /path/to/pkhex-mgba-live-bridge/src/build_source.py --output-root "$HOME/Library/Caches/PKHeXBridgeBuilds"
```
Supported flags: `--dotnet`, `--qt-root`, `--lua-root`, and `--output-root`. Each run creates an isolated timestamped output directory, extracts the published single-file bundle to verify all 6,767 Chinese character mappings, and runs `codesign --verify --strict --deep` on the nested `.app` bundle.

### 2. Automated Regression Suites

#### Python & Lua Protocol / Save Rollback Tests (No external `.sav` required)
```bash
python3 -m venv .test-venv
.test-venv/bin/python -m pip install -r src/tests/requirements.txt
.test-venv/bin/python -m unittest test_bridge.py test_protocol.py test_save_commit.py test_mgba_socket.py -v
```
> If no local `.sav` file is present, `test_bridge.py` and `test_save_commit.py` automatically synthesize a valid 128 KiB + 16-byte RTC Emerald save image in memory.

#### C# Bridge, 3-Way Merge & Coordinated Save Regressions
```bash
dotnet run --project src/tests/native/check.csproj -c Release
```
Covers encryption roundtrips, semantic field patching, party/box slot swaps with live HP preservation, stale save counter rejection, TCP lost-receipt idempotency, coordinated disk race protection, and 3-way conflict resolution.

#### Frontend xUnit Headless Tests (`PreparedWriteTests`)
```bash
dotnet run --project <build-output>/editor-src/Tests/PKHeX.Avalonia.Tests/PKHeX.Avalonia.Tests.csproj -c Release -- -class '*PreparedWriteTests'
```
Covers trainer header notifications, save bank (Bank 0 ⇄ Bank 1) rotation while preserving active workspace and box navigation, unapplied draft retention across live refreshes, Event Flag draft tracking/live refresh, and save writer context disposal.

#### Real mGBA Native Core Flash Rollback Test
```bash
python3 src/tests/flash-rollback/run.py --native-build '<build-output>/native'
```
Loads [`Resources/save_commit.lua`](./Resources/save_commit.lua) directly against the compiled `libmgba` dynamic library on a temporary save copy, verifying that save replacement failures after savestate loads restore the original disk backend, deferred-write Flash mask, Flash bank register, RAM, and frame counter.

#### 6,767 Chinese Character Codec Check
```bash
dotnet run --project src/tests/chinese/check.csproj -c Release -p:ChineseAssemblyDir='<build-output>/bundle-check' -- src/tools/ChineseCompat/ChineseMap.tsv
```

### 3. Component Overview

- **[`BridgeEngine.cs`](./BridgeEngine.cs)**: Save section parser, Pokémon substructure encryptor/decryptor, semantic field patch generator, and TCP/Unix socket RPC client.
- **[`BridgeModule.cs`](./BridgeModule.cs)**: Avalonia toolbar/status UI and central save hook.
- **[`SaveCommitCoordinator.cs`](./SaveCommitCoordinator.cs)**: Manages coordinated save transactions, lock leases, and pending transaction journals.
- **[`LiveWorkspace.cs`](./LiveWorkspace.cs) & [`LiveMerge.cs`](./LiveMerge.cs)**: Public host connection API, 3-way field merge (`Rebase`), Pokémon identity tracking across party/box slot moves, and full 14-sector save image reconstruction.
- **[`Resources/mgba_bridge.lua`](./Resources/mgba_bridge.lua) & [`Resources/save_commit.lua`](./Resources/save_commit.lua)**: Protocol v3 server, `GUARDALL` precondition checks, `gTasks` Bag quantity live redraw, and atomic Flash/disk backend switching with savestate rollback.
- **[`tools/MgbaBridge`](./tools/MgbaBridge)**: C++/Qt integration (`LiveBridge.cpp`) for automatic script loading, `CoreController::Interrupter` thread-safe execution, and Unix socket session publishing.
- **[`tools/SourceEditor`](./tools/SourceEditor)**: PKHeX-Avalonia frontend integration (`AutoLiveWorkspace.cs`, `LiveViewModels.cs`).
- **[`tools/ChineseCompat`](./tools/ChineseCompat)**: 6,767-character double-byte Chinese encoding/decoding for the 2011 Chinese Emerald translation.

---

<a id="中文说明"></a>

## 中文说明

当前交付目标版本为 `1.101.2-cn5-preview`，通信协议版本为 3。

### 1. 源码实时预览版构建（`build_source.py`）

[`build_source.py`](./build_source.py) 从已核验的完整上游源码构建修改器和内置 mGBA。自有集成位于 [`tools/SourceEditor`](./tools/SourceEditor)、[`tools/MgbaBridge`](./tools/MgbaBridge)、[`LiveWorkspace.cs`](./LiveWorkspace.cs) 与 [`LiveMerge.cs`](./LiveMerge.cs)。

#### 第一步：拉取并校验上游源码
```bash
python3 src/tools/fetch_sources.py PKHeX-Avalonia
python3 src/tools/fetch_sources.py mgba
```

#### 第二步：执行构建与严格签名
```bash
cd /tmp
python3 /完整路径/pkhex-mgba-live-bridge/src/build_source.py --output-root "$HOME/Library/Caches/PKHeXBridgeBuilds"
```
构建入口支持 `--dotnet`、`--qt-root`、`--lua-root` 和 `--output-root`。每次构建使用独立输出目录，自动从单文件发布包提取程序集验证全部 6,767 个汉字映射，并对嵌套应用执行 `codesign --verify --strict --deep` 校验。

### 2. 自动化回归测试

#### Python 与 Lua 协议/回滚测试（无需外部存档）
```bash
python3 -m venv .test-venv
.test-venv/bin/python -m pip install -r src/tests/requirements.txt
.test-venv/bin/python -m unittest test_bridge.py test_protocol.py test_save_commit.py test_mgba_socket.py -v
```

#### C# 桥接、三方合并与协调保存回归
```bash
dotnet run --project src/tests/native/check.csproj -c Release
```

#### 前端 xUnit 回归测试（`PreparedWriteTests`）
```bash
dotnet run --project <构建输出目录>/editor-src/Tests/PKHeX.Avalonia.Tests/PKHeX.Avalonia.Tests.csproj -c Release -- -class '*PreparedWriteTests'
```

#### 真实 mGBA 内核 Flash 回滚故障回归
```bash
python3 src/tests/flash-rollback/run.py --native-build '<构建输出目录>/native'
```

#### 6,767 字中文字表兼容性检查
```bash
dotnet run --project src/tests/chinese/check.csproj -c Release -p:ChineseAssemblyDir='<构建输出目录>/bundle-check' -- src/tools/ChineseCompat/ChineseMap.tsv
```

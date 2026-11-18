# PKHeX → mGBA Live Bridge User Guide / 使用说明

**[English](#english) | [中文说明](#中文说明)**

---

<a id="english"></a>

## English

Adapted for **Pokémon Emerald (`BPEE`, including the 2011 Chinese 386 ROM hack)** using **PKHeX-Avalonia** and **mGBA 0.10.5** (Protocol v3).

### 1. Source Live Preview Edition (`PKHeX实时版预览.app`)

Open `/Applications/PKHeX实时版预览.app`. It automatically launches the embedded emulator and restores your most recently selected game. Once you enter the overworld in-game, the editor automatically loads your current Trainer, Bag, Party, PC Boxes, and Event Flags.

1. **Switching Games**: Click **"打开游戏…" (Open Game…)** in the main window toolbar and select the `.gba` file.
2. **Applying Edits**: After editing Trainer info, Bag items, or Event Flags, click **"应用 / Save"** in that panel. After editing a Pokémon, use the slot's **"设置 (Set)"** action to apply it to the workspace.
3. **Saving to Game & Disk**: Click **"保存 (Save)"** in the main window. The editor verifies the active game state, resolves any concurrent conflicts, and atomically writes to both live emulator memory and disk. If both the game and the editor modified the same field, a dialog lists the **Baseline**, **Live Game Value**, and **Edited Value** so you can choose which to keep.
4. **Timeline Changes**: When loading a savestate, resetting, or switching games, unapplied drafts are archived before prompting to load the new timeline.

> **Note**: The preview edition enables **HaX mode** by default, allowing display and editing of 999 stats. Click **"实时游戏状态" (Live Game Status)** in the toolbar to view connection details and diagnostic logs.

### 2. Integrated Bridge Mode (`PKHeX中文兼容.app` cn4)

1. Open `/Applications/PKHeX中文兼容.app` and load your game's `.sav` file via **File → Open**.
2. Click **"实时桥接" (Live Bridge)** in the top-right corner, select the matching `.gba` ROM, and click **"启动桥接" (Start Bridge)**.
3. Open the same game in `mGBA` and enter the overworld.
4. Click **"复制连接行" (Copy Connection Line)** in the bridge panel. In `mGBA`, open **Tools → Scripting**, paste the line into the input box, and click **Run**. Close the scripting window and return to the game.
5. Once the panel shows **"mGBA 已连接" (mGBA Connected)**, edit your Pokémon/Bag, apply changes to the workspace, and click **Save** in the main window.

### 3. Standalone Python Script Mode (`bridge.py`)

1. Double-click [`start_live_bridge.command`](./start_live_bridge.command) or run:
   ```bash
   python3 bridge.py --rom "/path/to/game.gba" --save "/path/to/game.sav"
   ```
2. Continue your game in `mGBA`, open **Tools → Scripting**, paste the `dofile(...)` line printed in the terminal, and click **Run**.
3. Open the same `.sav` file in PKHeX, edit, apply, and click **Save**. When the terminal prints `已即时同步`, your edits are active in the running game.

### 4. Consistency & Safety Protections

- **Full Semantic Field Guards**: Money checks all 4 bytes; Pokémon edits check complete 80/100-byte records; commits verify the live XOR encryption key, Trainer ID, party count, and save block pointers. Concurrent changes trigger an automatic re-read and retry.
- **Coordinated Save & Stale Editor Protection**: The emulator verifies both the full disk image and live Flash snapshot inside the synchronous core callback before patching RAM, rebinding the save backend, and atomically replacing the `.sav` file. If a save failure occurs after savestate loading (where Flash differs from disk), the rollback restores the original disk backend, deferred-write Flash mask, and Flash bank state without data loss.
- **Idempotent Transactions**: Each logical edit uses a stable transaction ID. If a reply is lost over the socket, the client queries completed receipts before re-submitting so subsequent in-game spending is never overwritten.
- **Timeline Invalidation**: Resetting, loading a savestate, rewinding frames, or reloading the script increments the timeline epoch and isolates stale drafts.

### 5. Known Limitations

- Double-byte Chinese characters occupy 2 bytes each. Trainer names and OT names hold up to 7 bytes (3 Chinese characters); Pokémon nicknames hold up to 10 bytes (5 Chinese characters).
- In-game Bag non-zero quantity changes redraw automatically while browsing the Bag. Adding/removing item slots or modifying other open menus may require closing and reopening the menu in-game to refresh cached UI text.
- Modifying map coordinates, script flags, or Battle Frontier facility state while map scripts are actively running is best followed by an in-game save and reload.
- Abnormal stats (such as 999 in HaX mode) remain subject to the game's own stat recalculation when leveling up or depositing into PC boxes.

---

<a id="中文说明"></a>

## 中文说明

适配 **`GBA口袋妖怪绿宝石386汉化版.gba`（底本 `BPEE`）**，使用 **PKHeX-Avalonia** 和 **mGBA 0.10.5**，连接脚本协议版本为 3。

### 1. 源码实时预览版（`PKHeX实时版预览.app`）

打开 `/Applications/PKHeX实时版预览.app`。它会启动内置模拟器并恢复最近选择的游戏，进入游戏后自动显示当前人物、背包、宝可梦与事件标记。

1. **更换游戏**：点击主窗口“打开游戏…”，选择对应 `.gba` 文件。
2. **应用修改**：编辑训练家、背包或事件标记后，点击面板中的“应用 / Save”。编辑宝可梦后，通过对应槽位的“设置”操作应用到工作区。
3. **保存生效**：点击主窗口“保存”，应用会核对当前游戏、处理冲突并写入运行数据和磁盘。双方同时改了同一字段时，窗口列出原值、游戏值和编辑值供选择。
4. **时间线保护**：读取即时存档、重置或切换游戏时，旧草稿会先保留，再提示读取新时间线。

> **说明**：预览版默认启用 HaX 编辑模式，可显示和编辑 999 能力值。查看状态时点击顶部“实时游戏状态”。

### 2. 中文兼容版 cn4 的内置桥接启动方式

1. 打开 `/Applications/PKHeX中文兼容.app`，通过“文件 → 打开”载入当前游戏的 `.sav` 存档。
2. 点击窗口右上角“实时桥接”，选择与存档配套的 `.gba` ROM，点击“启动桥接”。
3. 在 mGBA 打开同一份游戏并继续游戏。
4. 点击面板中的“复制连接行”。在 mGBA 打开“工具 → 脚本”，将整行粘贴到输入框并点击“运行”，随后关闭脚本窗口返回游戏。
5. 面板出现“mGBA 已连接”后，在修改器里编辑并应用，最后点击顶部“保存”。

### 3. 原有独立脚本的启动方式（`bridge.py`）

1. 双击 [`start_live_bridge.command`](./start_live_bridge.command)，或运行：
   ```bash
   python3 bridge.py --rom "/完整路径/游戏.gba" --save "/完整路径/游戏.sav"
   ```
2. 在 mGBA 中继续游戏，打开“工具 → 脚本”，将终端显示的 `dofile(...)` 整行粘贴到输入框并点击“运行”。
3. 在 PKHeX 打开同一个 `.sav` 文件，编辑、应用并保存。终端出现“已即时同步”表示修改已写入运行中的游戏。

### 4. 一致性保护与数据同步

- **完整字段与前置条件校验**：金钱核对全部 4 字节，宝可梦核对完整记录，提交同时校验运行加密密钥、训练家身份、队伍数量、保存计数（`0x03006200`）和保存块指针。
- **协调保存与失败无损回滚**：模拟器在同步回调内核对完整磁盘与 Flash 镜像，再修改内存、切换后端并原子替换存档文件。读取即时存档后，即使内存中的 Flash 与磁盘版本不同，保存替换失败也会恢复提交前的磁盘后端、延后写盘 Flash 掩码、Flash Bank 与运行状态。
- **幂等事务 ID**：一次逻辑编辑使用稳定事务 ID。回执丢失后，客户端先查询已完成结果再推进基准，保留后续游戏内消费。
- **时间线感知**：重置、读取即时存档、帧回退或重新加载脚本会使旧事务失效并自动归档隔离草稿。

### 5. 已知限制

- 汉字通常占 2 字节。训练家名与原训练家名容量为 7 字节（最多 3 个纯汉字），宝可梦昵称容量为 10 字节（最多 5 个纯汉字），中英文混合按实际字节占用计算。
- 背包普通浏览状态下，修改已有道具的非零数量会自动重绘画面；新增/删除道具或其他界面缓存可通过重新打开游戏内菜单刷新。
- 修改地图位置、剧情状态和设施数据时，运行中的脚本可能仍持有缓存，涉及这些状态的改动适合保存后重新载入游戏。
- 999 等超常能力值仍会受到游戏自身升级或箱子存取时的能力重算机制影响。

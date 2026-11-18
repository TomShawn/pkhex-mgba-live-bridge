-- Local bridge for the Emerald BPEE memory layout, mGBA 0.10+.
-- Protocol v3. Clients construct guarded edits. This side commits and records results.

local generation = gbaLiveBridge and ((gbaLiveBridge.generation or 0) + 1) or 1
if gbaLiveBridge and gbaLiveBridge.close then
    gbaLiveBridge:close()
end

local config = assert(GBA_BRIDGE_CONFIG, "Start bridge.py before loading the connection script")
local bridge = {
    clients = {}, callbacks = {}, server = nil, generation = generation,
    epoch = 0, results = {}, acknowledged = {}, result_count = 0, last_frame = nil,
    session = (config.session or "bridge") .. "_" .. generation .. "_" .. tostring({}):gsub("[^%w]", ""),
}
gbaLiveBridge = bridge
local disk = config.save_module and dofile(config.save_module) or nil

local hex_codes = {}
for value = 0, 255 do hex_codes[string.char(value)] = string.format("%02x", value) end
local function hex(data)
    return (data:gsub(".", function(character)
        return hex_codes[character]
    end))
end

local function unhex(data)
    assert(#data % 2 == 0 and not data:find("[^0-9a-f]"), "Invalid hex data")
    return (data:gsub("..", function(pair)
        return string.char(tonumber(pair, 16))
    end))
end

local restore_menu_refresh
local function invalidate()
    if restore_menu_refresh then restore_menu_refresh(true) end
    bridge.epoch = bridge.epoch + 1
    bridge.results = {}
    bridge.acknowledged = {}
    bridge.result_count = 0
    bridge.last_frame = emu and emu:currentFrame() or nil
    bridge.trainer_id = nil
    for client in pairs(bridge.clients) do client.transaction = nil end
end

local function track_timeline()
    if not emu then return end
    local frame = emu:currentFrame()
    if bridge.last_frame and frame < bridge.last_frame then invalidate() end
    bridge.last_frame = frame
    if config.native and emu:read32(0x030022C0) == 0x08085E05 then
        local small = emu:read32(0x03005D90)
        if small >= 0x02000000 and small + 0xF2C <= 0x02040000 then
            local trainer = string.unpack("<I4", emu:readRange(small + 10, 4))
            if bridge.trainer_id and bridge.trainer_id ~= trainer then invalidate() end
            bridge.trainer_id = trainer
        end
    end
end

local function same_timeline(session, epoch)
    track_timeline()
    return session == bridge.session and tonumber(epoch) == bridge.epoch
end

local function pointers()
    return emu:read32(0x03005D8C), emu:read32(0x03005D90), emu:read32(0x03005D94)
end

local function valid_pointer(address, length)
    return address >= 0x02000000 and address + length <= 0x02040000 and address % 4 == 0
end

local function ready()
    if not emu then return false end
    if config.native and emu:read32(0x030022C0) ~= 0x08085E05 then return false end
    local large, small, boxes = pointers()
    return valid_pointer(large, 0x3D88)
        and valid_pointer(small, 0xF2C)
        and valid_pointer(boxes, 0x83D0)
        and emu:read8(0x020244E9) >= 1
        and emu:read8(0x020244E9) <= 6
        and (emu:read8(0x030026F9) & 2) == 0
end

local function allowed(address, length, large, small, boxes)
    for _, region in ipairs({{large, 0x3D88}, {small, 0xF2C}, {boxes, 0x83D0}, {0x020244EC, 600}, {0x020244E9, 1}}) do
        if address >= region[1] and address + length <= region[1] + region[2] then
            return true
        end
    end
    return false
end

local function write(address, data)
    for index = 1, #data do
        emu:write8(address + index - 1, data:byte(index))
    end
end

-- Verified for this ROM hash. ListMenu's task signature matches RedrawListMenu.
-- Schedule redraw through the game's task loop so its window renderer runs in
-- the normal game context, then restore the list's original idle function.
local function bag_list_tasks(visitor)
    for index = 0, 15 do
        local address = 0x03005E00 + index * 40
        if emu:read8(address + 4) == 1
            and emu:read32(address + 12) == 0x081AB451
            and emu:read32(address + 16) == 0x081AB521 then
            visitor(address)
        end
    end
end

restore_menu_refresh = function(force)
    if not config.native then return end
    if not force and bridge.menu_refresh_frame and emu:currentFrame() < bridge.menu_refresh_frame then return end
    bag_list_tasks(function(address)
        if emu:read32(address) == 0x081AE70D then write(address,string.pack("<I4",0x081AE459)) end
    end)
    bridge.menu_refresh_frame = nil
end

local function refresh_bag_quantities(large, small, before)
    if not before then return end
    local after = emu:readRange(large + 0x560,0x2E8)
    if before == after then return end
    local key = emu:read32(small + 0xAC) & 0xFFFF
    for offset = 1, #before, 4 do
        local old_id, old_count = string.unpack("<I2I2",before,offset)
        local new_id, new_count = string.unpack("<I2I2",after,offset)
        if old_id ~= new_id or ((old_count ~ key) == 0) ~= ((new_count ~ key) == 0) then return end
    end
    local browsing = false
    for index = 0, 15 do
        local address = 0x03005E00 + index * 40
        if emu:read8(address + 4) == 1 and emu:read32(address) == 0x081ABD29 then browsing = true end
    end
    if not browsing then return end
    bag_list_tasks(function(address)
        local current = emu:read32(address)
        if current == 0x081AE459 or current == 0x081AE70D then
            bridge.menu_refresh_frame = emu:currentFrame() + 2
            write(address,string.pack("<I4",0x081AE70D))
        end
    end)
end

local function disconnect(client)
    client.closed = true
    bridge.clients[client] = nil
    pcall(function() client.socket:close() end)
end

local function flush(client)
    local remaining = client.output
    if #remaining == 0 then return end
    local count, error_code = client.socket:send(remaining:sub(1, 16384))
    if count and count > 0 then
        client.output = remaining:sub(count + 1)
    elseif error_code and error_code ~= socket.ERRORS.AGAIN then
        disconnect(client)
    end
end

local function respond(client, text)
    client.output = client.output .. text .. "\n"
    flush(client)
end

local function snapshot(client, include_flash)
    track_timeline()
    if not ready() then
        respond(client, "WAIT game-not-ready")
        return
    end
    local large, small, boxes = pointers()
    local pieces = {
        string.format("STATE %d %x %x %x %s %d", emu:currentFrame(), large, small, boxes, bridge.session, bridge.epoch),
        "SMALL " .. hex(emu:readRange(small, 0xF2C)),
        "LARGE " .. hex(emu:readRange(large, 0x3D88)),
        "BOXES " .. hex(emu:readRange(boxes, 0x83D0)),
        "PARTY " .. hex(string.char(emu:read8(0x020244E9)) .. emu:readRange(0x020244EC, 600)),
    }
    if include_flash then
        assert(disk and disk.snapshot, "Live Flash snapshot is unavailable")
        pieces[#pieces + 1] = "FLASH " .. hex(disk.snapshot())
        pieces[#pieces + 1] = "COUNTER " .. hex(emu:readRange(0x03006200, 4))
        pieces[#pieces + 1] = "DISKBYTES " .. hex(disk.read(config.save_path))
    end
    pieces[#pieces + 1] = "END"
    respond(client, table.concat(pieces, "\n"))
end

local function commit(client, identity)
    local transaction = client.transaction
    client.transaction = nil
    assert(transaction and transaction.id == identity, "Missing transaction")
    if not same_timeline(transaction.session, transaction.epoch) then
        respond(client, "STALE " .. identity)
        return
    end
    if bridge.results[identity] then
        respond(client, bridge.results[identity])
        return
    end
    if bridge.result_count >= 4096 then
        for completed in pairs(bridge.acknowledged) do
            bridge.results[completed] = nil
            bridge.acknowledged[completed] = nil
            bridge.result_count = bridge.result_count - 1
            if bridge.result_count < 4096 then break end
        end
    end
    assert(bridge.result_count < 4096, "Transaction capacity reached; confirm pending receipts")
    assert(ready(), "Game is busy")
    local large, small, boxes = pointers()
    assert(large == transaction.large and small == transaction.small and boxes == transaction.boxes, "Save blocks moved; retry")
    for _, patch in ipairs(transaction.patches) do
        assert(allowed(patch.address, #patch.before, large, small, boxes), "Address outside supported data")
        assert(emu:readRange(patch.address, #patch.before) == patch.before, "Live data changed; retry")
    end
    if transaction.guard then
        local guard = transaction.guard
        assert(emu:readRange(small, 0xF2C) == guard.small
            and emu:readRange(large, 0x3D88) == guard.large
            and emu:readRange(boxes, 0x83D0) == guard.boxes
            and string.char(emu:read8(0x020244E9)) .. emu:readRange(0x020244EC, 600) == guard.party,
            "Snapshot changed before commit; retry")
        assert(not guard.counter or emu:read32(0x03006200) == guard.counter,
            "Snapshot changed before commit; save counter advanced")
    end
    local save_plan
    if transaction.disk then
        assert(disk, "Coordinated save module missing; reload connection script")
        save_plan = disk.prepare(config.save_path, identity, transaction.disk.expected, transaction.disk.desired, transaction.disk.flash)
    end
    local bag_before = config.native and emu:readRange(large + 0x560,0x2E8) or nil
    local written = {}
    local success, failure = pcall(function()
        for _, patch in ipairs(transaction.patches) do
            written[#written + 1] = patch
            write(patch.address, patch.after)
            assert(emu:readRange(patch.address, #patch.after) == patch.after, "Write verification failed")
        end
        if save_plan then save_plan:finish() end
    end)
    if not success then
        if save_plan then
            local restored, rollback_error = pcall(function() save_plan:rollback() end)
            if not restored then failure = tostring(failure) .. " " .. tostring(rollback_error) end
        end
        for index = #written, 1, -1 do
            write(written[index].address, written[index].before)
        end
        error(failure)
    end
    local result = string.format("APPLIED %s %d %d", identity, #transaction.patches, emu:currentFrame())
    bridge.results[identity] = result
    if config.native then bridge.trainer_id = string.unpack("<I4", emu:readRange(small + 10, 4)) end
    bridge.result_count = bridge.result_count + 1
    pcall(refresh_bag_quantities,large,small,bag_before)
    respond(client, result)
    console:log(string.format("PKHeX bridge: applied %d patches at frame %d", #transaction.patches, emu:currentFrame()))
end

local function command(client, line)
    if not client.authenticated then
        assert(line == "AUTH " .. config.token, "Authentication failed")
        assert(emu, "No game loaded")
        local crc = hex(emu:checksum(C.CHECKSUM.CRC32))
        assert(crc == config.crc, "ROM differs from bridge configuration")
        client.authenticated = true
        track_timeline()
        respond(client, string.format("HELLO %s %s %d 3", crc, bridge.session, bridge.epoch))
        console:log("PKHeX bridge: local client connected")
        return
    end
    local status_id, status_session, status_epoch = line:match("^STATUS (%x+) ([%w_]+) (%d+)$")
    if status_id then
        if not same_timeline(status_session, status_epoch) then
            respond(client, "STALE " .. status_id)
        else
            respond(client, bridge.results[status_id] or ("UNKNOWN " .. status_id))
        end
        return
    end
    local ack_id, ack_session, ack_epoch = line:match("^ACK (%x+) ([%w_]+) (%d+)$")
    if ack_id then
        if not same_timeline(ack_session, ack_epoch) then respond(client, "STALE " .. ack_id) return end
        assert(bridge.results[ack_id], "Unknown receipt")
        bridge.acknowledged[ack_id] = true
        respond(client, "ACKED " .. ack_id)
        return
    end
    if line == "SNAP" then
        snapshot(client)
        return
    end
    if line == "SNAPLIVE" then
        snapshot(client, true)
        return
    end
    if line == "CAPS" then
        respond(client, "CAPABILITIES live-snapshot-guard flash-guard save-counter disk-snapshot")
        return
    end
    if line == "CONTEXT" then
        respond(client, string.format("CONTEXT %x %x %d", emu:read32(0x030022C0), emu:read32(0x030022C4), ready() and 1 or 0))
        return
    end
    if line == "DIAG" then
        respond(client, "REGION " .. hex(emu:readRange(0x03005D00, 0x1000)))
        return
    end
    if line == "PING" then
        track_timeline()
        respond(client, string.format("PONG %d %d", emu:currentFrame(), ready() and 1 or 0))
        return
    end
    if line == "STOP" then
        bridge:close()
        console:log("PKHeX bridge: stopped by editor")
        return
    end
    local identity, session, epoch, large, small, boxes = line:match("^BEGIN (%x+) ([%w_]+) (%d+) (%x+) (%x+) (%x+)$")
    if identity then
        client.transaction = {id = identity, session = session, epoch = tonumber(epoch), large = tonumber(large, 16), small = tonumber(small, 16), boxes = tonumber(boxes, 16), patches = {}}
        return
    end
    local disk_before, disk_after = line:match("^DISK (%x+) (%x+)$")
    if disk_before then
        local transaction = assert(client.transaction, "Missing transaction")
        assert(not transaction.disk, "Duplicate disk image")
        transaction.disk = {expected = unhex(disk_before), desired = unhex(disk_after)}
        return
    end
    local disk_live_before, flash_before, disk_live_after = line:match("^DISKLIVE (%x+) (%x+) (%x+)$")
    if disk_live_before then
        local transaction = assert(client.transaction, "Missing transaction")
        assert(not transaction.disk, "Duplicate disk image")
        transaction.disk = {expected = unhex(disk_live_before), flash = unhex(flash_before), desired = unhex(disk_live_after)}
        return
    end
    local guard_small, guard_large, guard_boxes, guard_party, guard_counter = line:match("^GUARDALL (%x+) (%x+) (%x+) (%x+) (%x+)$")
    if not guard_small then guard_small, guard_large, guard_boxes, guard_party = line:match("^GUARDALL (%x+) (%x+) (%x+) (%x+)$") end
    if guard_small then
        local transaction = assert(client.transaction, "Missing transaction")
        assert(not transaction.guard, "Duplicate snapshot guard")
        local guard = {small = unhex(guard_small), large = unhex(guard_large), boxes = unhex(guard_boxes), party = unhex(guard_party), counter = guard_counter and tonumber(guard_counter, 16)}
        assert(#guard.small == 0xF2C and #guard.large == 0x3D88 and #guard.boxes == 0x83D0 and #guard.party == 601,
            "Invalid snapshot guard size")
        transaction.guard = guard
        return
    end
    local address, before, after = line:match("^PATCH (%x+) (%x+) (%x+)$")
    if address then
        local transaction = assert(client.transaction, "Missing transaction")
        assert(#transaction.patches < 50000, "Too many patches")
        before, after = unhex(before), unhex(after)
        assert(#before == #after and #before <= 256, "Invalid patch length")
        transaction.patches[#transaction.patches + 1] = {address = tonumber(address, 16), before = before, after = after}
        return
    end
    identity = line:match("^COMMIT (%x+)$")
    if identity then
        commit(client, identity)
        return
    end
    error("Unknown command")
end

local function receive(client)
    while not client.closed do
        local data, error_code = client.socket:receive(8192)
        if not data then
            if error_code ~= socket.ERRORS.AGAIN then disconnect(client) end
            return
        end
        if #data == 0 then
            disconnect(client)
            return
        end
        client.input = client.input .. data
        if #client.input > 2000000 then
            disconnect(client)
            return
        end
        while not client.closed do
            local ending = client.input:find("\n", 1, true)
            if not ending then break end
            local line = client.input:sub(1, ending - 1)
            client.input = client.input:sub(ending + 1)
            local success, failure = pcall(command, client, line)
            if not success then
                client.transaction = nil
                respond(client, "ERROR " .. tostring(failure):gsub("[^%w %-%._]", " "))
                if not client.authenticated then disconnect(client) return end
            end
        end
    end
end

local function accept()
    local connection = bridge.server:accept()
    if not connection then return end
    local client = {socket = connection, input = "", output = "", authenticated = false}
    bridge.clients[client] = true
    connection:add("received", function() receive(client) end)
    connection:add("error", function() disconnect(client) end)
end

function bridge:close()
    restore_menu_refresh(true)
    for client in pairs(self.clients) do disconnect(client) end
    if self.server then self.server:close() self.server = nil end
    for _, callback in ipairs(self.callbacks) do callbacks:remove(callback) end
    self.callbacks = {}
end

-- The native host executes a whole request while holding CoreController's
-- interruption guard. The same adapter also serves the migration TCP client.
bridge.native_clients = {}
function bridge:nativeRequest(identity, commands)
    if commands == "DETACH" then
        local client = self.native_clients[identity]
        if client then disconnect(client) end
        self.native_clients[identity] = nil
        if self.native_owner == identity then self.native_owner = nil end
        return "DETACHED\n"
    end
    local client = self.native_clients[identity]
    local pieces = {}
    if not client then
        client = {input = "", output = "", authenticated = false}
        self.native_clients[identity] = client
        self.clients[client] = true
    end
    client.socket = {
        send = function(_, data) pieces[#pieces + 1] = data return #data end,
        close = function() end,
    }
    for line in (commands .. "\n"):gmatch("([^\n]+)\n") do
        local success, failure = pcall(function()
            assert(line ~= "STOP", "Native bridge lifetime belongs to the emulator")
            if line:match("^BEGIN ") then
                assert(client.authenticated, "Authenticate first")
                assert(not self.native_owner or self.native_owner == identity, "Another editor holds the editing lease")
                self.native_owner = identity
            end
            command(client, line)
        end)
        if not success then
            client.transaction = nil
            respond(client, "ERROR " .. tostring(failure):gsub("[^%w %-%._]", " "))
            break
        end
    end
    while #client.output > 0 do flush(client) end
    return table.concat(pieces)
end

if not config.native then
    local server, bind_error = socket.bind("127.0.0.1", config.port)
    assert(server, "Cannot bind local bridge socket " .. tostring(bind_error))
    bridge.server = server
    assert(server:listen())
    server:add("received", accept)
end
bridge.callbacks[#bridge.callbacks + 1] = callbacks:add("frame", function()
    track_timeline()
    restore_menu_refresh(false)
    if config.native then return end
    for client in pairs(bridge.clients) do
        flush(client)
        -- Socket event polling can omit an EOF with no remaining payload.
        -- The nonblocking receive also detects that half-closed connection.
        if not client.closed then receive(client) end
    end
end)
for _, event in ipairs({"reset", "stateLoaded", "start", "stop"}) do
    bridge.callbacks[#bridge.callbacks + 1] = callbacks:add(event, invalidate)
end
console:log(config.native and "PKHeX native bridge ready" or ("PKHeX bridge v3 listening on localhost:" .. config.port))

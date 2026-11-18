-- mGBA 0.10.5 provides raw savestate snapshots with a SAVEDATA extension.
-- These calls execute in the same synchronous core callback as COMMIT.
local module = {}

local function read_file(path)
    local file = assert(io.open(path, "rb"), "Cannot open save file")
    local data = assert(file:read("a"), "Cannot read save file")
    assert(file:close(), "Cannot close save file")
    return data
end

local function flash_snapshot()
    local raw = assert(emu:saveStateBuffer(0), "Cannot capture core state")
    local state = assert(emu:saveStateBuffer(2), "Cannot capture Flash state")
    assert(#state >= #raw + 32, "Missing savedata extension")
    local tag, size, offset = string.unpack("<I4I4I8", state, #raw + 1)
    assert(tag == 2 and size == 0x20000 and offset == #raw + 32,
        "Unsupported mGBA savedata extension")
    assert(offset + size == #state, "Invalid savedata snapshot length")
    return state:sub(offset + 1, offset + size), state, raw
end

module.snapshot = flash_snapshot
module.read = read_file

function module.prepare(path, identity, expected, desired, expected_flash)
    assert(type(path) == "string" and #path > 0, "Save path missing from bridge configuration")
    assert((#expected == 0x20000 or #expected == 0x20010) and #desired == #expected,
        "Invalid save image size")
    assert(read_file(path) == expected, "Save conflict: disk changed after editor validation")
    assert(not expected_flash or #expected_flash == 0x20000, "Invalid expected Flash size")
    local original_flash, original_state, original_raw = flash_snapshot()
    assert(original_flash == (expected_flash or expected:sub(1, 0x20000)),
        "Save conflict: emulator Flash changed after editor validation")

    local plan = { path = path, stage = path .. ".pkhex-" .. identity .. ".tmp", loaded = false, done = false }
    local file = assert(io.open(plan.stage, "wb"), "Cannot stage save image")
    local ok, failure = pcall(function()
        assert(file:write(desired), "Cannot write staged save image")
        assert(file:flush(), "Cannot flush staged save image")
    end)
    local closed = file:close()
    if not ok or not closed then
        os.remove(plan.stage)
        error(failure or "Cannot close staged save image")
    end

    function plan:finish()
        -- Loading the new backing file unmaps and closes the previous cache.
        -- Rename then gives the new backing inode the real save filename.
        self.loaded = assert(emu:loadSaveFile(self.stage, false), "Cannot bind staged save image")
        assert(flash_snapshot() == desired:sub(1, 0x20000), "Staged Flash verification failed")
        assert(read_file(self.path) == expected, "Save conflict while rebinding save backend")
        assert(os.rename(self.stage, self.path), "Cannot replace save image")
        self.done = true
    end

    function plan:rollback()
        if self.done then return end
        if self.loaded then
            assert(emu:loadSaveFile(self.path, false), "Cannot restore original save backend; staged save retained")
            -- Reloading the file restores its backend, but its bytes may differ
            -- from a previously loaded savestate. Restore the zero-frame
            -- checkpoint with flags=0: mGBA masks savedata in memory and writes
            -- it back only after a later game save. flags=2 would overwrite the
            -- original disk mapping immediately.
            assert(emu:loadStateBuffer(original_state, 0), "Cannot restore pre-commit state; staged save retained")
            -- mGBA installs the savedata mask after deserializing the core and
            -- resets its Flash bank. Reload the raw checkpoint (no savedata
            -- extension) to restore bank/command state on the restored mask.
            assert(emu:loadStateBuffer(original_raw, 0), "Cannot restore pre-commit Flash state; staged save retained")
            assert(flash_snapshot() == original_flash, "Restored Flash verification failed; staged save retained")
        end
        os.remove(self.stage)
    end
    return plan
end

return module

local module = dofile(test_module)
local disk = module.read(test_save)
local flash = module.snapshot()
assert(disk:sub(1, 0x20000) ~= flash, "Fixture needs distinct disk and Flash")
emu:write32(0x02000000, 1234)
local frame = emu:currentFrame()
local plan = module.prepare(test_save, "native_audit", disk, disk, flash)
local rename = os.rename
os.rename = function() return nil, "injected rename failure" end
local success, failure = pcall(function() plan:finish() end)
os.rename = rename
assert(not success and failure:find("Cannot replace save image", 1, true))
emu:write32(0x02000000, 5678)
plan:rollback()
assert(module.snapshot() == flash, "Flash changed after rollback")
assert(module.read(test_save) == disk, "Rollback overwrote disk")
assert(emu:read32(0x02000000) == 1234, "RAM checkpoint not restored")
assert(emu:currentFrame() == frame, "Rollback changed frame")

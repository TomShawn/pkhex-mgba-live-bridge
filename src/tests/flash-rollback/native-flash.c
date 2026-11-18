// Run the real save_commit.lua against the compiled mGBA core, without a GUI.
#include <mgba/core/core.h>
#include <mgba/core/scripting.h>
#include <mgba/internal/defines.h>
#include <mgba/internal/gba/gba.h>
#include <mgba/internal/script/lua.h>
#include <mgba/script/context.h>
#include <mgba/script/types.h>
#include <mgba-util/vfs.h>

static void check(bool value, const char* label) {
    if (!value) { fprintf(stderr, "FAIL %s\n", label); exit(1); }
}

int main(int argc, char** argv) {
    check(argc == 5, "arguments: ROM temporary-save module scenario");
    struct mCore* core = mCoreFind(argv[1]);
    check(core && core->init(core), "initialize core");
    mCoreInitConfig(core, NULL);
    check(mCoreLoadFile(core, argv[1]), "load ROM");
    color_t* pixels = calloc(256 * 256, sizeof(color_t));
    core->setVideoBuffer(core, pixels, 256);
    core->reset(core);
    check(mCoreLoadSaveFile(core, argv[2], false), "load temporary disk save");
    struct GBA* gba = core->board;
    GBASavedataForceType(&gba->memory.savedata, SAVEDATA_FLASH1M);
    uint8_t* before = malloc(0x20000);
    memcpy(before, gba->memory.savedata.data, 0x20000);
    before[0x100] ^= 1;
    check(core->savedataRestore(core, before, 0x20000, false), "create distinct Flash timeline");
    gba->memory.savedata.currentBank = gba->memory.savedata.data + 0x10000;

    struct mScriptContext context;
    mScriptContextInit(&context);
    mScriptContextAttachStdlib(&context);
    struct mScriptEngineContext* lua = mScriptContextRegisterEngine(&context, mSCRIPT_ENGINE_LUA);
    mScriptContextAttachCore(&context, core);
    mScriptContextSetGlobal(&context, "test_save", mScriptStringCreateFromUTF8(argv[2]));
    mScriptContextSetGlobal(&context, "test_module", mScriptStringCreateFromUTF8(argv[3]));
    struct VFile* script = VFileOpen(argv[4], O_RDONLY);
    check(script && lua->load(lua, argv[4], script), "load native test scenario");
    script->close(script);
    if (!lua->run(lua)) { fprintf(stderr, "%s\n", lua->getError(lua)); return 1; }
    check(memcmp(gba->memory.savedata.data, before, 0x20000) == 0, "restored native Flash");
    check(gba->memory.savedata.currentBank == gba->memory.savedata.data + 0x10000, "restored native Flash bank");
    check(gba->memory.savedata.maskWriteback, "deferred writeback remains enabled");
    // Exercise the actual delayed game-save flush, not a replacement test stub.
    gba->memory.savedata.data[0x200] ^= 1;
    before[0x200] ^= 1;
    gba->memory.savedata.dirty = mSAVEDATA_DIRT_NEW;
    GBASavedataClean(&gba->memory.savedata, 100);
    GBASavedataClean(&gba->memory.savedata, 1000);
    struct VFile* disk = VFileOpen(argv[2], O_RDONLY);
    uint8_t* persisted = malloc(0x20000);
    check(disk && disk->read(disk, persisted, 0x20000) == 0x20000, "read later game save");
    disk->close(disk);
    check(memcmp(persisted, before, 0x20000) == 0, "later native flush reaches original save path");
    mScriptContextDeinit(&context);
    mCoreConfigDeinit(&core->config);
    core->deinit(core);
    free(before); free(persisted); free(pixels);
    puts("PASS actual mGBA Lua rollback, disk preservation, Flash bank, RAM/frame and later save flush");
    return 0;
}

"""Create an isolated mGBA source tree with the local bridge integration."""

from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[2]


def replace(path, before, after):
    text = path.read_text()
    if text.count(before) != 1:
        raise RuntimeError(f"Upstream integration point changed in {path.name}")
    path.write_text(text.replace(before, after))


def prepare(target):
    if target.exists():
        raise RuntimeError(f"Source staging directory already exists at {target}")
    shutil.copytree(ROOT / "upstream/mgba", target)
    qt = target / "src/platform/qt"
    for name in ("LiveBridge.cpp", "LiveBridge.h"):
        shutil.copy2(Path(__file__).parent / name, qt / "scripting" / name)
    replace(qt / "CMakeLists.txt", "\t\tscripting/ScriptingController.cpp", "\t\tscripting/LiveBridge.cpp\n\t\tscripting/ScriptingController.cpp")
    replace(qt / "CMakeLists.txt", "set(CMAKE_OSX_DEPLOYMENT_TARGET ${MIN_VER})", "set(CMAKE_OSX_DEPLOYMENT_TARGET 15.0)")
    replace(qt / "scripting/ScriptingController.h", "class CoreController;", "class CoreController;\nclass LiveBridge;")
    replace(qt / "scripting/ScriptingController.h", "\tbool loadFile(const QString& path);", "\tbool loadFile(const QString& path);\n\tvoid startBridge(const QString& rom);\n\tQByteArray bridgeRequest(const QString& identity, const QString& commands);")
    replace(qt / "scripting/ScriptingController.h", "\tstd::shared_ptr<CoreController> m_controller;", "\tstd::shared_ptr<CoreController> m_controller;\n\tstd::unique_ptr<LiveBridge> m_liveBridge;\n\tQString m_bridgeRom;")
    controller = qt / "scripting/ScriptingController.cpp"
    replace(controller, '#include "scripting/ScriptingController.h"', '#include "scripting/ScriptingController.h"\n#include "scripting/LiveBridge.h"')
    replace(controller, "void ScriptingController::clearController() {", "void ScriptingController::clearController() {\n\tm_liveBridge.reset();\n\tm_bridgeRom.clear();")
    replace(controller, "void ScriptingController::reset() {", "void ScriptingController::reset() {\n\tQString bridgeRom = m_bridgeRom;\n\tm_liveBridge.reset();")
    replace(controller, '\nvoid ScriptingController::runCode(const QString& code) {', '\nvoid ScriptingController::runCode(const QString& code) {')
    # Reset has one final core reattachment. Recreate the transport only after
    # the new engine has attached, so old timeline transactions become stale.
    replace(controller, '\t\tmScriptContextAttachCore(&m_scriptContext, m_controller->thread()->core);\n\t}\n}\n\nvoid ScriptingController::runCode', '\t\tmScriptContextAttachCore(&m_scriptContext, m_controller->thread()->core);\n\t}\n\tif (!bridgeRom.isEmpty()) startBridge(bridgeRom);\n}\n\nvoid ScriptingController::runCode')
    with controller.open("a") as output:
        output.write('''
void ScriptingController::startBridge(const QString& rom) {
    if (m_liveBridge || !m_controller) return;
    m_bridgeRom = rom;
    auto bridge = std::make_unique<LiveBridge>(this, m_controller, rom);
    if (bridge->start()) m_liveBridge = std::move(bridge);
}

QByteArray ScriptingController::bridgeRequest(const QString& identity, const QString& commands) {
    if (!m_controller || !m_activeEngine || !m_controller->hasStarted()) return "ERROR Game unavailable\\n";
    CoreController::Interrupter interrupted(m_controller);
    QString code = "GBA_NATIVE_REPLY=gbaLiveBridge:nativeRequest(" + bridgeLuaQuote(identity) + "," + bridgeLuaQuote(commands) + ")";
    VFileDevice script(code.toUtf8());
    if (!m_activeEngine->load(m_activeEngine, "*native-bridge", script) || !m_activeEngine->run(m_activeEngine))
        return "ERROR Adapter request failed\\n";
    auto* value = m_activeEngine->getGlobal(m_activeEngine, "GBA_NATIVE_REPLY");
    QByteArray reply;
    if (value && value->type->base == mSCRIPT_TYPE_STRING) {
        reply = QByteArray(value->value.string->buffer, value->value.string->size);
    }
    mScriptContextDrainPool(&m_scriptContext);
    m_activeEngine->setGlobal(m_activeEngine, "GBA_NATIVE_REPLY", nullptr);
    return reply.isEmpty() ? QByteArray("ERROR Empty adapter reply\\n") : reply;
}
''')
    replace(qt / "Window.cpp", "void Window::gameStarted() {", '''void Window::gameStarted() {
#ifdef ENABLE_SCRIPTING
    if (!m_scripting) m_scripting = std::make_unique<ScriptingController>();
    m_scripting->setController(m_controller);
    m_scripting->startBridge(windowFilePath());
#endif''')
    replace(qt / "main.cpp", "#include <QTranslator>", "#include <QTranslator>\n#include <QTimer>")
    replace(qt / "main.cpp", "\tw->argumentsPassed();\n\n\tw->show();", "\tw->show();\n\tQTimer::singleShot(0,w,&Window::argumentsPassed);")
    socket = target / "src/script/socket.c"
    replace(socket, "\t\tSocketClose(ssock->socket);", "\t\tSocket descriptor = ssock->socket;\n\t\tssock->socket = INVALID_SOCKET;\n\t\tSocketClose(descriptor);")
    replace(socket, "if (bytes <= 0)", "if (bytes < 0)")
    vfs = target / "src/util/vfs/vfs-fd.c"
    with vfs.open("a") as output:
        output.write('''
bool VFileGetNativePath(struct VFile* vf, char* output, size_t capacity) {
#ifdef __APPLE__
    if (!vf || vf->close != _vfdClose || capacity < PATH_MAX) return false;
    return fcntl(((struct VFileFD*) vf)->fd, F_GETPATH, output) == 0;
#else
    UNUSED(vf);
    UNUSED(output);
    UNUSED(capacity);
    return false;
#endif
}
''')
    return target


if __name__ == "__main__":
    import sys
    print(prepare(Path(sys.argv[1]).resolve()))

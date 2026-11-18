#pragma once

#include <QObject>
#include <QLocalServer>
#include <memory>

namespace QGBA {
class CoreController;
class ScriptingController;

// Lifetime belongs to one loaded game. All requests run on the Qt thread,
// under the scripting controller's core interruption guard.
class LiveBridge : public QObject {
public:
    LiveBridge(ScriptingController*, std::shared_ptr<CoreController>, const QString& rom);
    ~LiveBridge();
    bool start();

private:
    void accept();
    ScriptingController* m_scripting;
    std::shared_ptr<CoreController> m_core;
    QLocalServer m_server;
    QString m_rom;
    QString m_socket;
    QString m_manifest;
    QString m_token;
    QString m_instance;
    QString m_savePath;
    int m_saveLease = -1;
    quint64 m_nextClient = 0;
};

QString bridgeLuaQuote(const QString& value);
}

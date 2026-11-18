#include "scripting/LiveBridge.h"
#include "scripting/ScriptingController.h"
#include "CoreController.h"

#include <QCoreApplication>
#include <QCryptographicHash>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QJsonDocument>
#include <QJsonObject>
#include <QLocalSocket>
#include <QSaveFile>
#include <QStandardPaths>
#include <QTimer>
#include <QProcess>
#include <QMessageBox>
#include <QUuid>
#include <QtEndian>

#include <mgba/internal/gba/gba.h>
#include <sys/stat.h>
#include <sys/file.h>
#include <fcntl.h>
#include <unistd.h>

extern "C" bool VFileGetNativePath(struct VFile*, char*, size_t);

using namespace QGBA;

namespace {
constexpr quint32 MaxMessage = 2000000;
const char* RomHash = "af821cf6f017b41241f2be73fe9bba34a1e411fdc3da235e333bd0890b9b3195";

bool startupFailure(const char* stage) {
    QString state = QStandardPaths::writableLocation(QStandardPaths::GenericDataLocation) + "/PKHeX-Avalonia/GBABridge";
    QDir().mkpath(state);
    QFile log(state + "/native-start.log");
    auto mode = log.size() > 32768 ? QIODevice::Truncate : QIODevice::Append;
    if (log.open(QIODevice::WriteOnly | mode)) {
        log.setPermissions(QFile::ReadOwner | QFile::WriteOwner);
        log.write(QByteArray::number(QCoreApplication::applicationPid()) + " " + stage + "\n");
    }
    return false;
}

bool privateDirectory(const QString& path) {
    QByteArray bytes = QFile::encodeName(path);
    if (::mkdir(bytes.constData(), 0700) != 0 && errno != EEXIST) return false;
    struct stat info;
    if (::lstat(bytes.constData(), &info) != 0 || !S_ISDIR(info.st_mode) || info.st_uid != getuid()) return false;
    return ::chmod(bytes.constData(), 0700) == 0;
}

QByteArray frame(const QJsonObject& value) {
    QByteArray body = QJsonDocument(value).toJson(QJsonDocument::Compact);
    QByteArray result(4, 0);
    qToBigEndian<quint32>(body.size(), reinterpret_cast<uchar*>(result.data()));
    result.append(body);
    return result;
}

QString backendPath(const std::shared_ptr<CoreController>& core) {
    CoreController::Interrupter interrupted(core);
    auto* gba = static_cast<GBA*>(core->thread()->core->board);
    auto& save = gba->memory.savedata;
    char path[PATH_MAX];
    VFile* file = save.realVf ? save.realVf : save.vf;
    if (!VFileGetNativePath(file,path,sizeof(path))) return {};
    return QFileInfo(QString::fromUtf8(path)).canonicalFilePath();
}
}

QString QGBA::bridgeLuaQuote(const QString& value) {
    QByteArray result("\"");
    for (unsigned char byte : value.toUtf8()) {
        if (byte == '"' || byte == '\\') {
            result.append('\\');
            result.append(byte);
        } else if (byte < 32 || byte > 126) {
            result.append('\\');
            result.append(QByteArray::number(byte).rightJustified(3, '0'));
        } else {
            result.append(byte);
        }
    }
    result.append('"');
    return QString::fromLatin1(result);
}

LiveBridge::LiveBridge(ScriptingController* scripting, std::shared_ptr<CoreController> core, const QString& rom)
    : QObject(scripting), m_scripting(scripting), m_core(std::move(core)), m_rom(rom) {
    connect(&m_server, &QLocalServer::newConnection, this, &LiveBridge::accept);
}

LiveBridge::~LiveBridge() {
    // Disconnect callbacks before any child socket destruction can access Lua.
    for (QLocalSocket* client : findChildren<QLocalSocket*>()) client->disconnect(this);
    m_server.close();
    if (!m_manifest.isEmpty()) QFile::remove(m_manifest);
    if (!m_socket.isEmpty()) QLocalServer::removeServer(m_socket);
    if (m_saveLease >= 0) ::close(m_saveLease);
}

bool LiveBridge::start() {
    if (!m_core || m_core->platform() != mPLATFORM_GBA) return false;
    QFile rom(m_rom);
    if (!rom.open(QIODevice::ReadOnly)) return startupFailure("rom-open");
    QCryptographicHash hash(QCryptographicHash::Sha256);
    if (!hash.addData(&rom) || hash.result().toHex() != RomHash) return startupFailure("rom-profile");

    QString save = backendPath(m_core);
    if (save.isEmpty()) return startupFailure("save-backend-path");
    QProcess owners;
    owners.start(QStringLiteral("/usr/sbin/lsof"),{"-a","-c","mGBA","-t","--",save});
    if (owners.waitForFinished(2000)) {
        for (const auto& line : owners.readAllStandardOutput().split('\n')) {
            bool valid;
            qint64 pid = line.trimmed().toLongLong(&valid);
            if (valid && pid != QCoreApplication::applicationPid()) {
                m_core->stop();
                QMessageBox::warning(nullptr,QStringLiteral("存档已在使用"),QStringLiteral("这份存档已有运行中的游戏。请保存并关闭原游戏窗口后重试。"));
                return false;
            }
        }
    } else {
        owners.kill();
        owners.waitForFinished();
    }

    QString resources = qEnvironmentVariable("PKHEX_BRIDGE_RESOURCES");
    if (resources.isEmpty()) resources = QCoreApplication::applicationDirPath() + "/../Resources/GBABridge";
    if (!QFileInfo::exists(resources + "/mgba_bridge.lua") || !QFileInfo::exists(resources + "/save_commit.lua")) return startupFailure("bridge-resources");

    QString sockets = QString("/tmp/pkx-%1").arg(getuid());
    if (!privateDirectory(sockets)) return false;
    QString leasePath = sockets + "/save-" + QCryptographicHash::hash(save.toUtf8(),QCryptographicHash::Sha256).toHex() + ".lock";
    m_saveLease = ::open(QFile::encodeName(leasePath).constData(),O_CREAT | O_RDWR | O_CLOEXEC,0600);
    if (m_saveLease < 0) return false;
    if (::flock(m_saveLease,LOCK_EX | LOCK_NB) != 0) {
        emit m_scripting->warn(QStringLiteral("同一份存档已有运行中的游戏，本次重复会话已停止。"));
        m_core->stop();
        QMessageBox::warning(nullptr,QStringLiteral("存档已在使用"),QStringLiteral("这份存档已有运行中的游戏，本次重复会话已停止。"));
        return false;
    }
    m_savePath = save;
    m_instance = QUuid::createUuid().toString(QUuid::Id128);
    m_token = QUuid::createUuid().toString(QUuid::Id128) + QUuid::createUuid().toString(QUuid::Id128);
    m_socket = sockets + "/" + m_instance.left(16) + ".sock";
    if (m_socket.toUtf8().size() >= 104) return false;
    m_server.setSocketOptions(QLocalServer::UserAccessOption);
    if (!m_server.listen(m_socket)) return false;

    QString config = "GBA_BRIDGE_CONFIG={native=true,port=0,crc=\"ad89a894\",token=" + bridgeLuaQuote(m_token)
        + ",session=" + bridgeLuaQuote(m_instance) + ",save_path=" + bridgeLuaQuote(save)
        + ",save_module=" + bridgeLuaQuote(resources + "/save_commit.lua") + "}\n";
    m_scripting->runCode(config + "dofile(" + bridgeLuaQuote(resources + "/mgba_bridge.lua") + ")");
    if (!m_scripting->bridgeRequest("0", "AUTH " + m_token).startsWith("HELLO ")) return startupFailure("adapter-handshake");
    m_scripting->bridgeRequest("0", "DETACH");

    QString state = QStandardPaths::writableLocation(QStandardPaths::GenericDataLocation) + "/PKHeX-Avalonia/GBABridge";
    QDir().mkpath(state);
    QString sessions = state + "/sessions";
    if (!privateDirectory(sessions)) return false;
    m_manifest = sessions + "/" + m_instance + ".json";
    QJsonObject endpoint{
        {"Protocol", 3}, {"Port", 0}, {"Token", m_token}, {"Crc", "ad89a894"},
        {"SocketPath", m_socket}, {"SavePath", save}, {"RomPath", QFileInfo(m_rom).canonicalFilePath()},
        {"RomSha256", QString::fromLatin1(RomHash)}, {"InstanceId", m_instance},
        {"Pid", static_cast<double>(QCoreApplication::applicationPid())}
    };
    QSaveFile manifest(m_manifest);
    if (!manifest.open(QIODevice::WriteOnly)) return false;
    manifest.setPermissions(QFile::ReadOwner | QFile::WriteOwner);
    QByteArray bytes = QJsonDocument(endpoint).toJson(QJsonDocument::Compact);
    return manifest.write(bytes) == bytes.size() && manifest.commit();
}

void LiveBridge::accept() {
    while (m_server.hasPendingConnections()) {
        QLocalSocket* socket = m_server.nextPendingConnection();
        socket->setParent(this);
        socket->setReadBufferSize(MaxMessage + 4);
        QString identity = QString::number(++m_nextClient);
        auto buffer = std::make_shared<QByteArray>();
        auto idle = new QTimer(socket);
        idle->setSingleShot(true);
        idle->setInterval(30000);
        connect(idle, &QTimer::timeout, socket, &QLocalSocket::abort);
        idle->start();
        connect(socket, &QLocalSocket::disconnected, this, [this, socket, identity]() {
            m_scripting->bridgeRequest(identity, "DETACH");
            socket->deleteLater();
        });
        connect(socket, &QLocalSocket::readyRead, this, [this, socket, identity, buffer, idle]() {
            idle->start();
            if (backendPath(m_core) != m_savePath) {
                socket->abort();
                QTimer::singleShot(0,m_scripting,&ScriptingController::reset);
                return;
            }
            buffer->append(socket->readAll());
            if (buffer->size() > static_cast<int>(MaxMessage + 4)) { socket->abort(); return; }
            while (buffer->size() >= 4) {
                quint32 size = qFromBigEndian<quint32>(reinterpret_cast<const uchar*>(buffer->constData()));
                if (size == 0 || size > MaxMessage) { socket->abort(); return; }
                if (buffer->size() < static_cast<int>(size + 4)) return;
                QJsonParseError error;
                auto document = QJsonDocument::fromJson(buffer->mid(4, size), &error);
                buffer->remove(0, size + 4);
                if (error.error != QJsonParseError::NoError || !document.isObject()) { socket->abort(); return; }
                QString commands = document.object().value("commands").toString();
                if (commands.isEmpty()) { socket->abort(); return; }
                QByteArray reply = m_scripting->bridgeRequest(identity, commands);
                QByteArray response = frame(QJsonObject{{"data", QString::fromUtf8(reply)}});
                if (socket->bytesToWrite() + response.size() > MaxMessage) { socket->abort(); return; }
                socket->write(response);
            }
        });
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PKHeX.GbaBridge;

static void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

string boot = File.ReadAllText(args[0]);
int port = int.Parse(Regex.Match(boot, @"port=(\d+)").Groups[1].Value);
string token = Regex.Match(boot, "token=\"([a-f0-9]+)\"").Groups[1].Value;
string crc = Regex.Match(boot, "crc=\"([a-f0-9]+)\"").Groups[1].Value;
using var direct = new EmulatorClient(port, token, crc);

uint Money(Snapshot snapshot) => SaveCodec.U32(SaveCodec.Canonical(snapshot.Raw).Large, 0x490);
List<Patch> SetMoneyPlan(Snapshot snapshot, uint value)
{
    SaveData plain = SaveCodec.Canonical(snapshot.Raw);
    byte[] large = (byte[])plain.Large.Clone();
    SaveCodec.Put32(large, 0x490, value);
    return SaveCodec.Plan(snapshot.Raw, new SaveData(plain.Small, large, plain.Boxes), snapshot);
}
void SetMoney(uint value)
{
    Snapshot snapshot = direct.Snapshot();
    var pending = new PendingEdit([], snapshot.Raw, snapshot.Session, snapshot.Epoch);
    direct.Apply(snapshot, SetMoneyPlan(snapshot, value), pending);
}

SetMoney(1000);
Snapshot original = direct.Snapshot();
var patches = SetMoneyPlan(original, 1001);
var edit = new PendingEdit([], original.Raw, original.Session, original.Epoch);
SetMoney(744);
bool rejected = false;
try { direct.Apply(original, patches, edit); }
catch (BridgeException) { rejected = true; }
Check(rejected && Money(direct.Snapshot()) == 744, "real mGBA complete-field race rejected (744 preserved)");

SetMoney(1000);
original = direct.Snapshot();
patches = SetMoneyPlan(original, 1001);
edit = new PendingEdit([], original.Raw, original.Session, original.Epoch);
var proxy = new TcpListener(IPAddress.Loopback, 0);
proxy.Start();
int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
var forward = Task.Run(async () =>
{
    using var peer = await proxy.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
    using var upstream = new TcpClient();
    await upstream.ConnectAsync(IPAddress.Loopback, port);
    using var clientReader = new StreamReader(peer.GetStream());
    using var clientWriter = new StreamWriter(peer.GetStream()) { AutoFlush = true, NewLine = "\n" };
    using var serverReader = new StreamReader(upstream.GetStream());
    using var serverWriter = new StreamWriter(upstream.GetStream()) { AutoFlush = true, NewLine = "\n" };
    string? line;
    while ((line = await clientReader.ReadLineAsync()) is not null)
    {
        await serverWriter.WriteLineAsync(line);
        if (line.StartsWith("BEGIN ") || line.StartsWith("PATCH ")) continue;
        string reply = await serverReader.ReadLineAsync() ?? throw new IOException("Upstream closed");
        if (line.StartsWith("COMMIT "))
        {
            if (!reply.StartsWith("APPLIED ")) throw new Exception(reply);
            return; // Drop the actual APPLIED reply and disconnect both sides.
        }
        await clientWriter.WriteLineAsync(reply);
    }
});
bool lost = false;
try
{
    using var proxied = new EmulatorClient(proxyPort, token, crc);
    proxied.Apply(original, patches, edit);
}
catch (IOException) { lost = true; }
finally { proxy.Stop(); }
forward.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
Check(lost && Money(direct.Snapshot()) == 1001, "real mGBA write succeeded with reply dropped by TCP proxy");
SetMoney(1000); // Represents a game-side spend after the acknowledged write was lost.
using (var reconnected = new EmulatorClient(port, token, crc))
{
    reconnected.Apply(original, patches, edit);
    Check(Money(direct.Snapshot()) == 1000, "real mGBA reconnect recovered receipt and preserved later spend");
}

string signal = args[1];
Console.WriteLine("READY_FOR_STATE_LOAD");
while (!File.Exists(signal + ".state")) Thread.Sleep(100);
rejected = false;
try { direct.Result(edit); }
catch (StaleTransactionException) { rejected = true; }
Check(rejected, "real mGBA stateLoaded invalidated old transaction");
Snapshot afterLoad = direct.Snapshot();
var resetEdit = new PendingEdit([], afterLoad.Raw, afterLoad.Session, afterLoad.Epoch);
Console.WriteLine("READY_FOR_RESET");
while (!File.Exists(signal + ".reset")) Thread.Sleep(100);
rejected = false;
try { direct.Result(resetEdit); }
catch (StaleTransactionException) { rejected = true; }
Check(rejected, "real mGBA reset invalidated old transaction");
Console.WriteLine("Live fault injection completed.");

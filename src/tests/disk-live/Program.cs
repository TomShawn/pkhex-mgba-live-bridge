using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using PKHeX.GbaBridge;

var endpoint = JsonSerializer.Deserialize<BridgeEndpoint>(File.ReadAllBytes(args[0]))!;
string path = endpoint.SavePath;
string signal = args[1];
byte[] initial = File.ReadAllBytes(path);
SaveData parsed = SaveCodec.Parse(initial,true);
byte[] ChangeMoney(byte[] source, uint amount)
{
    // Patch the active valid sectors and regenerate checksums in place.
    SaveData before = SaveCodec.Parse(source,true);
    SaveData plain = SaveCodec.Canonical(before);
    byte[] large = (byte[])plain.Large.Clone();
    SaveCodec.Put32(large,0x490,amount);
    SaveData changed = SaveCodec.EncodeWithKey(new SaveData(plain.Small,large,plain.Boxes),SaveCodec.U32(before.Small,0xAC));
    byte[] result = (byte[])source.Clone();
    for (int offset = 0; offset < 28 * 4096; offset += 4096)
    {
        if (SaveCodec.U32(result,offset + 0xFF8) != 0x08012025 || SaveCodec.U32(result,offset + 0xFFC) != before.Counter) continue;
        int section = SaveCodec.U16(result,offset + 0xFF4);
        if (section is < 1 or > 4) continue;
        int start = SaveCodec.Sizes.Skip(1).Take(section - 1).Sum();
        byte[] payload = changed.Large[start..(start + SaveCodec.Sizes[section])];
        payload.CopyTo(result,offset);
        SaveCodec.Put16(result,offset + 0xFF6,SaveCodec.Checksum(payload));
    }
    return result;
}

var proxy = new TcpListener(IPAddress.Loopback,0);
proxy.Start();
var proxied = endpoint with { Port = ((IPEndPoint)proxy.LocalEndpoint).Port };
var forwarding = Task.Run(async () =>
{
    using var peer = await proxy.AcceptTcpClientAsync();
    using var upstream = new TcpClient();
    await upstream.ConnectAsync(IPAddress.Loopback,endpoint.Port);
    using var incoming = new StreamReader(peer.GetStream());
    using var outgoing = new StreamWriter(peer.GetStream()) { AutoFlush = true, NewLine = "\n" };
    using var serverReader = new StreamReader(upstream.GetStream());
    using var serverWriter = new StreamWriter(upstream.GetStream()) { AutoFlush = true, NewLine = "\n" };
    string? line;
    while ((line = await incoming.ReadLineAsync()) is not null)
    {
        if (line.StartsWith("COMMIT "))
        {
            Console.WriteLine("READY_FOR_GAME_SAVE_AFTER_VALIDATION");
            while (!File.Exists(signal + ".commit")) await Task.Delay(100);
        }
        await serverWriter.WriteLineAsync(line);
        if (line.StartsWith("BEGIN ") || line.StartsWith("PATCH ") || line.StartsWith("DISK ")) continue;
        string reply;
        do
        {
            reply = await serverReader.ReadLineAsync() ?? throw new IOException("Upstream disconnected");
            await outgoing.WriteLineAsync(reply);
        } while (line == "SNAP" && reply != "END" && !reply.StartsWith("WAIT "));
    }
});
bool rejected = false;
var coordinator = new SaveCommitCoordinator(Path.GetDirectoryName(args[0])!,180000);
try { coordinator.Write(path,ChangeMoney(initial,3333),proxied,parsed.Counter,true); }
catch (BridgeException error) { rejected = true; Console.WriteLine("EXPECTED " + error.Message); }
finally { proxy.Stop(); }
forwarding.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
byte[] gameSaved = File.ReadAllBytes(path);
if (!rejected || !SaveCodec.IsNewer(SaveCodec.Parse(gameSaved,true).Counter,parsed.Counter))
    throw new Exception("Concurrent real game save was not preserved");
Console.WriteLine("PASS real game save after editor validation rejected stale disk transaction");

coordinator.Write(path,ChangeMoney(gameSaved,3333),endpoint,SaveCodec.Parse(gameSaved,true).Counter,true);
using var direct = new EmulatorClient(endpoint.Port,endpoint.Token,endpoint.Crc);
if (SaveCodec.U32(SaveCodec.Canonical(direct.Snapshot().Raw).Large,0x490) != 3333)
    throw new Exception("RAM commit missing");
if (SaveCodec.U32(SaveCodec.Canonical(SaveCodec.Parse(File.ReadAllBytes(path),true)).Large,0x490) != 3333)
    throw new Exception("Disk commit missing");
Console.WriteLine("PASS coordinated RAM, Flash backing and disk save");
uint committedCounter = SaveCodec.Parse(File.ReadAllBytes(path),true).Counter;
Console.WriteLine("READY_FOR_FOLLOWING_GAME_SAVE");
while (!File.Exists(signal + ".next")) Thread.Sleep(100);
SaveData next = SaveCodec.Parse(File.ReadAllBytes(path),true);
if (!SaveCodec.IsNewer(next.Counter,committedCounter) || SaveCodec.U32(SaveCodec.Canonical(next).Large,0x490) != 3333)
    throw new Exception("Following game save used stale backing file");
Console.WriteLine("PASS following real game save kept edited money on the actual save path");

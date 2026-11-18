using System.Text.Json;
using PKHeX.GbaBridge;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

string state = args[0];
Check(LiveGameConnection.SamePath(state,LiveGameConnection.StateDirectory),"managed and native session directories match");
if (args.Contains("--paths-only")) return;
string savePath = Path.GetFullPath(args[1]);
var endpoint = NativeDiscovery.Find(state,savePath) ?? throw new Exception("Native session not found");
Check(endpoint.SocketPath is not null && endpoint.SavePath == savePath,"automatic discovery matches the actual save backend");
if (args.Contains("--faults"))
{
    await NativeFaults.Run(endpoint);
    return;
}
int timeline = Array.IndexOf(args,"--timeline");
if (timeline >= 0)
{
    Snapshot before;
    using (var client = new EmulatorClient(endpoint)) before = client.Snapshot();
    var old = new PendingEdit([],before.Raw,before.Session,before.Epoch);
    Console.WriteLine("READY_FOR_STATE_LOAD");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    while (!File.Exists(args[timeline + 1])) await Task.Delay(100,timeout.Token);
    bool stale = false;
    try
    {
        using var reconnected = new EmulatorClient(endpoint);
        reconnected.Result(old);
    }
    catch (StaleTransactionException) { stale = true; }
    Check(stale,"native state load invalidated the old transaction");
    var game = LiveGameConnection.Discover().Single(item => LiveGameConnection.SamePath(item.SavePath,savePath));
    var live = new LiveGameConnection(game);
    LiveSnapshot current = live.Read();
    Check(current.Timeline != before.Session + "/" + before.Epoch,"native state load published the new timeline");
    return;
}
int verify = Array.IndexOf(args,"--verify");
if (verify >= 0)
{
    uint money = uint.Parse(args[verify + 1]);
    ushort balls = ushort.Parse(args[verify + 2]);
    using var client = new EmulatorClient(endpoint);
    Snapshot current = client.Snapshot(true);
    SaveData runtime = SaveCodec.Canonical(SaveCodec.Effective(current));
    SaveData disk = SaveCodec.Canonical(SaveCodec.Parse(File.ReadAllBytes(savePath),true));
    Check(SaveCodec.U32(runtime.Large,0x490) == money && SaveCodec.U32(disk.Large,0x490) == money,"expected money in runtime and disk");
    Check(SaveCodec.U16(runtime.Large,0x650) == 1 && SaveCodec.U16(runtime.Large,0x652) == balls
        && SaveCodec.U16(disk.Large,0x650) == 1 && SaveCodec.U16(disk.Large,0x652) == balls,"expected Master Balls in runtime and disk");
    Check(SaveCodec.Parse(current.Flash!,true).Counter == current.SaveCounter,"Flash and RAM save counters agree");
    Console.WriteLine("PASS current save counter " + current.SaveCounter);
    return;
}
int dump = Array.IndexOf(args,"--dump-iwram");
if (dump >= 0)
{
    using var connection = new EmulatorClient(endpoint);
    File.WriteAllBytes(args[dump + 1],connection.Diagnostic());
    Console.WriteLine("PASS captured profile diagnostics");
    return;
}
for (int index = 0; index < 100; index++)
{
    using var connection = new EmulatorClient(endpoint);
    CheckReady(connection);
    if (index % 10 == 0) Check(connection.Snapshot().Raw.Boxes.Length == 0x83D0,"framed full snapshot");
}
Console.WriteLine("PASS 100 C# Unix socket connections");

using (var connection = new EmulatorClient(endpoint))
{
    int before = connection.Snapshot().Frame;
    await Task.Delay(1200);
    int after = connection.Snapshot().Frame;
    Check(args.Contains("--paused") ? after == before : after > before,"native requests preserve the original execution state");
}

if (args.Contains("--write"))
{
    byte[] image = File.ReadAllBytes(savePath);
    SaveData original = SaveCodec.Parse(image,true);
    uint key = SaveCodec.U32(original.Small,0xAC);
    for (int offset = 0; offset < 28 * 4096; offset += 4096)
    {
        if (SaveCodec.U16(image,offset + 0xFF4) != 1 || SaveCodec.U32(image,offset + 0xFFC) != original.Counter) continue;
        SaveCodec.Put32(image,offset + 0x490,4444 ^ key);
        SaveCodec.Put16(image,offset + 0xFF6,SaveCodec.Checksum(image[offset..(offset + SaveCodec.Sizes[1])]));
    }
    var coordinator = new SaveCommitCoordinator(Path.Combine(Path.GetDirectoryName(savePath)!,"native-test-state"));
    coordinator.Write(savePath,image,endpoint,original.Counter,true);
    Check(SaveCodec.U32(SaveCodec.Canonical(SaveCodec.Parse(File.ReadAllBytes(savePath),true)).Large,0x490) == 4444,
        "coordinated Unix submission persisted the expected money");
    using var connection = new EmulatorClient(endpoint);
    Check(SaveCodec.U32(SaveCodec.Canonical(connection.Snapshot().Raw).Large,0x490) == 4444,
        "coordinated Unix submission updated running memory");
}

if (args.Contains("--live"))
{
    var game = LiveGameConnection.Discover().Single(item => LiveGameConnection.SamePath(item.SavePath,savePath));
    var live = new LiveGameConnection(game);
    LiveSnapshot original = live.Read();
    var plain = SaveCodec.Canonical(SaveCodec.Parse(original.Image));
    SaveCodec.Put32(plain.Large,0x490,5555);
    byte[] desired = SaveCodec.Image(original.Image,SaveCodec.EncodeWithKey(plain,0));
    LiveSnapshot receipt = live.Save(original.Image,desired,original.Timeline,CancellationToken.None);
    if (args.Contains("--paused")) Check(receipt.Frame == original.Frame,"live submission preserves paused frame");
    Check(SaveCodec.U32(SaveCodec.Canonical(SaveCodec.Parse(receipt.Image)).Large,0x490) == 5555,"live workspace submission updated running data");
    Check(SaveCodec.U32(SaveCodec.Canonical(SaveCodec.Parse(File.ReadAllBytes(savePath))).Large,0x490) == 5555,"live workspace submission persisted the complete image");
}

static void CheckReady(EmulatorClient connection)
{
    if (!connection.Ping()) throw new Exception("Enter the test game before running the live checks");
}

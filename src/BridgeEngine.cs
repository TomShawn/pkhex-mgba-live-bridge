using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PKHeX.GbaBridge;

internal sealed class BridgeException(string message) : Exception(message);
internal sealed record SaveData(byte[] Small, byte[] Large, byte[] Boxes, uint Counter = 0);
internal sealed record Snapshot(int Frame, uint LargeAddress, uint SmallAddress, uint BoxAddress, SaveData Raw, byte[] Party, string Session = "", int Epoch = 0, byte[]? Flash = null, uint? SaveCounter = null, byte[]? Disk = null);
internal sealed record Patch(uint Address, byte[] Before, byte[] After);

internal static partial class SaveCodec
{
    internal const uint PartyAddress = 0x020244EC;
    internal const uint PartyCountAddress = 0x020244E9;
    internal static readonly int[] Sizes = [0xF2C, 0xF80, 0xF80, 0xF80, 0xF08, .. Enumerable.Repeat(0xF80, 8), 0x7D0];
    private static readonly int[][] Orders = BuildOrders();

    internal static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    internal static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    internal static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    internal static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    internal static ushort Checksum(byte[] bytes)
    {
        uint total = 0;
        for (int offset = 0; offset < bytes.Length; offset += 4)
            total = unchecked(total + U32(bytes, offset));
        return unchecked((ushort)((total & 0xFFFF) + (total >> 16)));
    }

    internal static SaveData Parse(byte[] data, bool requireLatestComplete = false)
    {
        if (data.Length is not (0x20000 or 0x20010))
            throw new BridgeException("需要 128 KiB 的游戏内存档，可附带 mGBA 时钟数据。");
        var candidates = new List<(uint Counter, Dictionary<int, byte[]> Sections)>();
        foreach (int start in new[] { 0, 14 * 4096 })
        {
            var sections = new Dictionary<int, byte[]>();
            var counters = new HashSet<uint>();
            for (int offset = start; offset < start + 14 * 4096; offset += 4096)
            {
                int number = U16(data, offset + 0xFF4);
                if (number >= 14 || U32(data, offset + 0xFF8) != 0x08012025 || sections.ContainsKey(number))
                    break;
                byte[] payload = data[offset..(offset + Sizes[number])];
                if (Checksum(payload) != U16(data, offset + 0xFF6))
                    break;
                sections[number] = payload;
                counters.Add(U32(data, offset + 0xFFC));
            }
            if (sections.Count == 14 && counters.Count == 1)
                candidates.Add((counters.Single(), sections));
        }
        if (candidates.Count == 0)
            throw new BridgeException("存档校验未通过，等待完整保存。");
        var chosen = candidates[0];
        foreach (var candidate in candidates.Skip(1))
            if (IsNewer(candidate.Counter, chosen.Counter))
                chosen = candidate;
        if (requireLatestComplete)
            for (int offset = 0; offset < 28 * 4096; offset += 4096)
                if (U32(data, offset + 0xFF8) == 0x08012025 && U16(data, offset + 0xFF4) < 14
                    && IsNewer(U32(data, offset + 0xFFC), chosen.Counter))
                    throw new BridgeException("较新的存档区仍在写入，等待完整保存。");
        return new SaveData(chosen.Sections[0],
            Enumerable.Range(1, 4).SelectMany(index => chosen.Sections[index]).ToArray(),
            Enumerable.Range(5, 9).SelectMany(index => chosen.Sections[index]).ToArray(), chosen.Counter);
    }

    internal static bool IsNewer(uint value, uint previous)
    {
        uint difference = unchecked(value - previous);
        return difference is > 0 and < 0x80000000;
    }

    private static int[][] BuildOrders()
    {
        var orders = new List<int[]>();
        for (int a = 0; a < 4; a++)
            for (int b = 0; b < 4; b++)
                for (int c = 0; c < 4; c++)
                    for (int d = 0; d < 4; d++)
                        if (new[] { a, b, c, d }.Distinct().Count() == 4)
                            orders.Add([a, b, c, d]);
        return orders.ToArray();
    }

    internal static byte[] DecodeMon(byte[] data)
    {
        uint pid = U32(data, 0), key = pid ^ U32(data, 4);
        var decrypted = new byte[48];
        for (int index = 0; index < 48; index += 4)
            Put32(decrypted, index, U32(data, 32 + index) ^ key);
        if ((data[19] & 2) != 0 && MonChecksum(decrypted) != U16(data, 28))
            throw new BridgeException("宝可梦记录校验未通过。");
        byte[] result = (byte[])data.Clone();
        int[] order = Orders[pid % 24];
        for (int position = 0; position < 4; position++)
            Array.Copy(decrypted, position * 12, result, 32 + order[position] * 12, 12);
        return result;
    }

    private static ushort MonChecksum(byte[] bytes)
    {
        int total = 0;
        for (int index = 0; index < 48; index += 2)
            total += U16(bytes, index);
        return unchecked((ushort)total);
    }

    internal static byte[] EncodeMon(byte[] data)
    {
        byte[] result = (byte[])data.Clone();
        uint pid = U32(data, 0), key = pid ^ U32(data, 4);
        var payload = new byte[48];
        int[] order = Orders[pid % 24];
        for (int position = 0; position < 4; position++)
            Array.Copy(data, 32 + order[position] * 12, payload, position * 12, 12);
        Put16(result, 28, MonChecksum(payload));
        for (int index = 0; index < 48; index += 4)
            Put32(result, 32 + index, U32(payload, index) ^ key);
        return result;
    }

    private static readonly (int Offset, int Size)[] MonFields =
    [
        (0,4),(4,4),(8,10),(18,1),(19,1),(20,7),(27,1),(30,2),
        (32,2),(34,2),(36,4),(40,1),(41,1),(42,2),
        .. Enumerable.Range(0,4).Select(index => (44+index*2,2)),
        .. Enumerable.Range(0,4).Select(index => (52+index,1)),
        .. Enumerable.Range(0,12).Select(index => (56+index,1)),
        (68,1),(69,1),(70,2),(72,4),(76,4)
    ];

    private static bool SameIdentity(byte[] first, byte[] second) => first.AsSpan(0, 8).SequenceEqual(second.AsSpan(0, 8));
    private static bool Occupied(byte[] mon) => (mon[19] & 2) != 0;

    internal static byte[] MergeMon(byte[] before, byte[] after, byte[] current)
    {
        if (before.SequenceEqual(after))
            return current;
        if (!SameIdentity(current, before) && !SameIdentity(current, after))
            throw new BridgeException("宝可梦槽位已在游戏内改变，请重新打开当前存档。");
        if (!Occupied(before) || !Occupied(after) || !SameIdentity(before, after))
            return after;
        byte[] old = DecodeMon(before), updated = DecodeMon(after), live = DecodeMon(current);
        IEnumerable<(int Offset, int Size)> fields = MonFields;
        if (before.Length == 100)
            fields = fields.Concat(new[] { (80,4),(84,1),(85,1) }).Concat(Enumerable.Range(0,7).Select(index => (86+index*2,2)));
        foreach (var (offset, size) in fields)
            if (!old.AsSpan(offset, size).SequenceEqual(updated.AsSpan(offset, size)))
                Array.Copy(updated, offset, live, offset, size);
        return EncodeMon(live);
    }

    internal static byte[] MergeSlots(byte[] before, byte[] after, byte[] current, int width)
    {
        byte[][] old = before.Chunk(width).ToArray(), updated = after.Chunk(width).ToArray(), live = current.Chunk(width).ToArray();
        var output = new List<byte>();
        for (int slot = 0; slot < old.Length; slot++)
        {
            byte[] previous = old[slot], active = live[slot];
            if (!SameIdentity(previous, updated[slot]) && Occupied(updated[slot]))
            {
                var previousMatches = old.Where(mon => Occupied(mon) && SameIdentity(mon, updated[slot])).ToArray();
                var activeMatches = live.Where(mon => Occupied(mon) && SameIdentity(mon, updated[slot])).ToArray();
                if (previousMatches.Length == 1 && activeMatches.Length == 1)
                    (previous, active) = (previousMatches[0], activeMatches[0]);
            }
            output.AddRange(MergeMon(previous, updated[slot], active));
        }
        return output.ToArray();
    }

    internal static SaveData Canonical(SaveData save)
    {
        byte[] small = (byte[])save.Small.Clone(), large = (byte[])save.Large.Clone();
        uint key = U32(small, 0xAC);
        Put32(small, 0xAC, 0);
        Put32(small, 0x1F4, U32(small, 0x1F4) ^ key);
        Put32(large, 0x490, U32(large, 0x490) ^ key);
        Put16(large, 0x494, (ushort)(U16(large, 0x494) ^ (ushort)key));
        for (int offset = 0x562; offset < 0x848; offset += 4)
            Put16(large, offset, (ushort)(U16(large, offset) ^ (ushort)key));
        for (int offset = 0x159C; offset < 0x169C; offset += 4)
            Put32(large, offset, U32(large, offset) ^ key);
        return new SaveData(small, large, save.Boxes);
    }

    internal static SaveData EncodeWithKey(SaveData plain, uint key)
    {
        byte[] small = (byte[])plain.Small.Clone();
        Put32(small, 0xAC, key);
        SaveData result = Canonical(new SaveData(small, plain.Large, plain.Boxes));
        Put32(result.Small, 0xAC, key);
        return result;
    }

    private static byte[] MergeRegion(byte[] before, byte[] after, byte[] current,
        IEnumerable<(int Offset, int Size)> fields, params (int Start, int End)[] bitRanges)
    {
        byte[] result = (byte[])current.Clone();
        var handled = new bool[before.Length];
        foreach (var (offset, size) in fields)
        {
            Array.Fill(handled, true, offset, size);
            if (!before.AsSpan(offset, size).SequenceEqual(after.AsSpan(offset, size)))
                Array.Copy(after, offset, result, offset, size);
        }
        for (int index = 0; index < before.Length; index++)
        {
            if (handled[index] || before[index] == after[index]) continue;
            if (bitRanges.Any(range => index >= range.Start && index < range.End))
            {
                int mask = before[index] ^ after[index];
                result[index] = (byte)((current[index] & ~mask) | (after[index] & mask));
            }
            else result[index] = after[index];
        }
        return result;
    }

    private static readonly (int,int)[] SmallFields = [(0,8),(8,1),(10,4),(14,5),(19,1),(20,2),(0x1F4,4)];
    private static readonly (int,int)[] LargeFields =
    [
        (0,4), .. Enumerable.Range(0,5).Select(index => (4+index*8,8)), (0x2C,2),(0x32,2),
        (0x234,4),(0x238,600),(0x490,4),(0x494,2),(0x496,2),
        .. Enumerable.Range(0,(0x848-0x498)/4).Select(index => (0x498+index*4,4)),
        .. Enumerable.Range(0,256).Select(index => (0x139C+index*2,2)),
        .. Enumerable.Range(0,64).Select(index => (0x159C+index*4,4))
    ];

    private static void AddPatches(List<Patch> patches, byte[] before, byte[] after, uint address,
        IEnumerable<(int Offset, int Size)> fields)
    {
        var covered = new bool[before.Length];
        foreach (var (offset, size) in fields)
        {
            Array.Fill(covered, true, offset, size);
            if (!before.AsSpan(offset, size).SequenceEqual(after.AsSpan(offset, size)))
                patches.Add(new Patch(address + (uint)offset, before[offset..(offset + size)], after[offset..(offset + size)]));
        }
        int index = 0;
        while (index < before.Length)
        {
            if (covered[index]) { index++; continue; }
            int end = index + 1;
            while (end < before.Length && !covered[end]) end++;
            if (!before.AsSpan(index,end - index).SequenceEqual(after.AsSpan(index,end - index)))
                for (int start = index; start < end; start += 256)
                {
                    int stop = Math.Min(start + 256,end);
                    patches.Add(new Patch(address + (uint)start,before[start..stop],after[start..stop]));
                }
            index = end;
        }
    }

    internal static string PreserveFile(string directory, string label, byte[] data)
    {
        Directory.CreateDirectory(directory);
        string digest = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()[..20];
        string path = Path.Combine(directory,label + "_" + digest + ".sav");
        if (!File.Exists(path)) File.WriteAllBytes(path,data);
        return path;
    }

    internal static void ValidateEditVersion(SaveData baseline, SaveData updated)
    {
        if (updated.Counter != baseline.Counter)
            throw new BridgeException("编辑存档版本已过期。已阻止写入，请从最新游戏存档备份重新打开并合并待提交修改。");
    }

    internal static List<Patch> Plan(SaveData before, SaveData after, Snapshot snapshot)
    {
        SaveData old = Canonical(before), updated = Canonical(after), live = Canonical(snapshot.Raw);
        if (!live.Small.AsSpan(10,4).SequenceEqual(old.Small.AsSpan(10,4))
            && !live.Small.AsSpan(10,4).SequenceEqual(updated.Small.AsSpan(10,4)))
            throw new BridgeException("当前运行的训练家与编辑存档不匹配。");
        byte[] small = MergeRegion(old.Small, updated.Small, live.Small, SmallFields, (0x28,0x90));
        byte[] large = MergeRegion(old.Large, updated.Large, live.Large, LargeFields, (0x988,0x9BC),(0x1270,0x139C),(0x3B24,0x3B58));
        byte count = old.Large[0x234] == updated.Large[0x234] ? snapshot.Party[0] : updated.Large[0x234];
        if (count is < 1 or > 6) throw new BridgeException("队伍数量需要为 1～6。");
        byte[] party = MergeSlots(old.Large[0x238..0x490], updated.Large[0x238..0x490], snapshot.Party[1..], 100);
        large[0x234] = count;
        party.CopyTo(large, 0x238);
        const int tail = 4 + 14 * 30 * 80;
        byte[] boxes = (byte[])live.Boxes.Clone();
        MergeRegion(old.Boxes[..4], updated.Boxes[..4], live.Boxes[..4], [(0,4)]).CopyTo(boxes, 0);
        MergeSlots(old.Boxes[4..tail], updated.Boxes[4..tail], live.Boxes[4..tail], 80).CopyTo(boxes, 4);
        MergeRegion(old.Boxes[tail..], updated.Boxes[tail..], live.Boxes[tail..], []).CopyTo(boxes, tail);
        SaveData encoded = EncodeWithKey(new SaveData(small, large, boxes), U32(snapshot.Raw.Small, 0xAC));
        var patches = new List<Patch>();
        var largeFields = LargeFields.Where(field => field != (0x238,600))
            .Concat(Enumerable.Range(0,6).Select(index => (0x238 + index * 100,100)));
        var boxFields = new[] { (0,4) }.Concat(Enumerable.Range(0,420).Select(index => (4 + index * 80,80)));
        AddPatches(patches, snapshot.Raw.Small, encoded.Small, snapshot.SmallAddress, SmallFields);
        AddPatches(patches, snapshot.Raw.Large, encoded.Large, snapshot.LargeAddress, largeFields);
        AddPatches(patches, snapshot.Raw.Boxes, encoded.Boxes, snapshot.BoxAddress, boxFields);
        AddPatches(patches, [snapshot.Party[0]], [count], PartyCountAddress, [(0,1)]);
        AddPatches(patches, snapshot.Party[1..], party, PartyAddress, Enumerable.Range(0,6).Select(index => (index * 100,100)));
        if (patches.Count > 0)
        {
            patches.Add(new Patch(snapshot.SmallAddress + 0xAC, snapshot.Raw.Small[0xAC..0xB0], snapshot.Raw.Small[0xAC..0xB0]));
            if (snapshot.Raw.Small.AsSpan(10,4).SequenceEqual(encoded.Small.AsSpan(10,4)))
                patches.Add(new Patch(snapshot.SmallAddress + 10, snapshot.Raw.Small[10..14], snapshot.Raw.Small[10..14]));
            if (count == snapshot.Party[0])
                patches.Add(new Patch(PartyCountAddress, [count], [count]));
        }
        return patches;
    }
}

internal sealed class StaleTransactionException(string message) : Exception(message);

internal sealed record PendingEdit(byte[] Data, SaveData Updated, string Session, int Epoch)
{
    internal byte[]? DiskExpected { get; init; }
    internal bool GuardSnapshot { get; init; }
    internal byte[]? DiskFlashExpected { get; init; }
    internal string Identity { get; init; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

internal sealed class EmulatorClient : IDisposable
{
    internal string Timeline { get; }
    private readonly Socket socket;
    private readonly NetworkStream stream;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly object sendGate = new();
    private readonly bool framed;
    private readonly Queue<string> replies = new();

    internal EmulatorClient(BridgeEndpoint endpoint, int receiveTimeout = 6000)
        : this(endpoint.Port,endpoint.Token,endpoint.Crc,receiveTimeout,endpoint.SocketPath) { }

    internal EmulatorClient(int port, string token, string crc, int receiveTimeout = 6000, string? socketPath = null)
    {
        framed = socketPath is not null;
        socket = new Socket(framed ? AddressFamily.Unix : AddressFamily.InterNetwork,SocketType.Stream,
            framed ? ProtocolType.Unspecified : ProtocolType.Tcp)
        { ReceiveTimeout = receiveTimeout, SendTimeout = 2000 };
        if (!framed) socket.NoDelay = true;
        try
        {
            EndPoint address = framed ? new UnixDomainSocketEndPoint(socketPath!) : new IPEndPoint(IPAddress.Loopback,port);
            socket.ConnectAsync(address).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            stream = new NetworkStream(socket,false);
            reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);
            writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, true) { AutoFlush = true, NewLine = "\n" };
            Send("AUTH " + token);
            string[] hello = Line().Split(' ');
            if (hello.Length != 5 || hello[0] != "HELLO" || hello[1] != crc || hello[4] != "3")
                throw new IOException("模拟器协议或身份校验失败，请重新加载新版连接脚本。");
            Timeline = hello[2] + "/" + hello[3];
        }
        catch { socket.Dispose(); throw; }
    }

    private string Line()
    {
        string line = framed ? (replies.TryDequeue(out string? reply) ? reply : throw new IOException("模拟器回复不完整。"))
            : reader.ReadLine() ?? throw new IOException("模拟器已断开。");
        if (line.Length > 400000) throw new IOException("模拟器返回数据过长。");
        return line.Trim();
    }

    private void Send(string value)
    {
        lock (sendGate)
        {
            if (!framed) { writer.WriteLine(value); return; }
            if (replies.Count != 0) throw new IOException("模拟器仍有未读取的回复。");
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { commands = value });
            if (body.Length > 2000000) throw new IOException("提交请求超过本机协议容量。");
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header,body.Length);
            stream.Write(header);
            stream.Write(body);
            stream.ReadExactly(header);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is <= 0 or > 2000000) throw new IOException("模拟器返回的消息长度无效。");
            byte[] response = new byte[length];
            stream.ReadExactly(response);
            using var document = JsonDocument.Parse(response);
            string data = document.RootElement.GetProperty("data").GetString() ?? throw new IOException("模拟器没有返回数据。");
            foreach (string line in data.Split('\n',StringSplitOptions.RemoveEmptyEntries)) replies.Enqueue(line);
        }
    }

    internal bool Ping()
    {
        Send("PING");
        string[] fields = Line().Split(' ');
        if (fields.Length != 3 || fields[0] != "PONG") throw new IOException("模拟器状态回复无效。");
        return fields[2] == "1";
    }

    internal void RequireLiveCapabilities()
    {
        Send("CAPS");
        if (Line() != "CAPABILITIES live-snapshot-guard flash-guard save-counter disk-snapshot")
            throw new LiveProtocolException("请使用应用内配套的实时模拟器，以启用当前快照与保存保护。");
    }

    internal byte[] Diagnostic()
    {
        Send("DIAG");
        string response = Line();
        if (!response.StartsWith("REGION ",StringComparison.Ordinal)) throw new IOException("游戏诊断回复无效。");
        return Convert.FromHexString(response[7..]);
    }

    internal Snapshot Snapshot(bool includeFlash = false)
    {
        Send(includeFlash ? "SNAPLIVE" : "SNAP");
        string[] header = Line().Split(' ');
        if (header[0] == "WAIT") throw new BridgeException("已连接，等待继续游戏或战斗结束。");
        if (header.Length != 7 || header[0] != "STATE") throw new IOException("模拟器状态回复无效。");
        var payload = new Dictionary<string,byte[]>();
        string line;
        while ((line = Line()) != "END")
        {
            int split = line.IndexOf(' ');
            if (split < 0) throw new IOException("模拟器数据不完整。");
            payload[line[..split]] = Convert.FromHexString(line[(split+1)..]);
        }
        foreach (var (name, size) in new[] { ("SMALL",0xF2C),("LARGE",0x3D88),("BOXES",0x83D0),("PARTY",601) })
            if (!payload.TryGetValue(name, out var bytes) || bytes.Length != size) throw new IOException("模拟器数据长度不匹配。");
        return new Snapshot(int.Parse(header[1]), Convert.ToUInt32(header[2],16), Convert.ToUInt32(header[3],16), Convert.ToUInt32(header[4],16),
            new SaveData(payload["SMALL"],payload["LARGE"],payload["BOXES"]), payload["PARTY"], header[5], int.Parse(header[6]),payload.GetValueOrDefault("FLASH"),
            payload.GetValueOrDefault("COUNTER") is { Length: 4 } counter ? SaveCodec.U32(counter,0) : null,payload.GetValueOrDefault("DISKBYTES"));
    }

    internal int? Result(PendingEdit pending)
    {
        Send($"STATUS {pending.Identity} {pending.Session} {pending.Epoch}");
        string line = Line();
        if (line == "STALE " + pending.Identity)
            throw new StaleTransactionException("游戏会话或时间线已改变，待提交修改已隔离，请基于当前游戏重新编辑。");
        string[] fields = line.Split(' ');
        if (fields.Length == 4 && fields[0] == "APPLIED" && fields[1] == pending.Identity)
            return int.Parse(fields[3]);
        if (line != "UNKNOWN " + pending.Identity) throw new IOException("事务查询回复无效。");
        return null;
    }

    internal void Acknowledge(PendingEdit pending)
    {
        Send($"ACK {pending.Identity} {pending.Session} {pending.Epoch}");
        if (Line() != "ACKED " + pending.Identity) throw new IOException("提交回执确认失败。");
    }

    internal int Apply(Snapshot snapshot, List<Patch> patches, PendingEdit pending)
    {
        if (Result(pending) is int completed) return completed;
        if (snapshot.Session != pending.Session || snapshot.Epoch != pending.Epoch)
            throw new StaleTransactionException("游戏时间线已变化，待提交修改已隔离。");
        string id = pending.Identity;
        var message = new StringBuilder($"BEGIN {id} {pending.Session} {pending.Epoch} {snapshot.LargeAddress:x} {snapshot.SmallAddress:x} {snapshot.BoxAddress:x}\n");
        foreach (var patch in patches)
            message.Append($"PATCH {patch.Address:x} {Convert.ToHexString(patch.Before).ToLowerInvariant()} {Convert.ToHexString(patch.After).ToLowerInvariant()}\n");
        if (pending.DiskExpected is not null)
        {
            string disk = Convert.ToHexString(pending.DiskExpected).ToLowerInvariant();
            string desired = Convert.ToHexString(pending.Data).ToLowerInvariant();
            message.Append(pending.DiskFlashExpected is null ? $"DISK {disk} {desired}\n"
                : $"DISKLIVE {disk} {Convert.ToHexString(pending.DiskFlashExpected).ToLowerInvariant()} {desired}\n");
        }
        if (pending.GuardSnapshot)
            message.Append($"GUARDALL {Convert.ToHexString(snapshot.Raw.Small).ToLowerInvariant()} {Convert.ToHexString(snapshot.Raw.Large).ToLowerInvariant()} {Convert.ToHexString(snapshot.Raw.Boxes).ToLowerInvariant()} {Convert.ToHexString(snapshot.Party).ToLowerInvariant()}"
                + (snapshot.SaveCounter is uint expectedCounter ? $" {expectedCounter:x8}" : string.Empty) + "\n");
        message.Append("COMMIT " + id);
        Send(message.ToString());
        string line = Line();
        if (line == "STALE " + id) throw new StaleTransactionException("游戏时间线已变化，待提交修改已隔离。");
        string[] reply = line.Split(' ');
        if (line.Contains("Save conflict",StringComparison.Ordinal))
            throw new BridgeException("游戏在提交前更新了存档。已保留最新游戏进度与编辑草稿，请刷新后重新保存。");
        if (reply.Length != 4 || reply[0] != "APPLIED" || reply[1] != id)
            throw new BridgeException("运行数据已变化，正在重新读取并重试。" + line);
        return int.Parse(reply[3]);
    }

    internal void StopServer()
    {
        if (framed) return;
        try { Send("STOP"); } catch { }
    }

    public void Dispose() => socket.Dispose();
}

internal sealed class BridgeSession : IDisposable
{
    internal string SavePath { get; }
    internal string RomPath { get; }
    internal string ConnectionLine { get; }
    internal uint LatestCounter => baseline.Counter;
    internal BridgeEndpoint Endpoint => new(port,token,crc,SavePath);
    private readonly CancellationTokenSource cancellation = new();
    private readonly List<FileStream> locks = [];
    private readonly Action<string,string> report;
    private readonly int port;
    private readonly string token;
    private readonly string crc;
    private byte[] baselineBytes;
    private SaveData baseline;
    private EmulatorClient? client;
    private Task? worker;
    private bool disposed;
    private readonly string recoveryDirectory;

    [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(int fd, int operation);

    internal BridgeSession(string romPath, string savePath, string luaPath, string stateDirectory, Action<string,string> report, CancellationToken preparation = default)
    {
        RomPath = Path.GetFullPath(romPath);
        SavePath = Path.GetFullPath(savePath);
        this.report = report;
        recoveryDirectory = Path.Combine(stateDirectory,"存档备份","版本保护");
        report("准备中","正在读取游戏 ROM。");
        byte[] rom = File.ReadAllBytes(RomPath);
        preparation.ThrowIfCancellationRequested();
        if (rom.Length < 0xB0 || Encoding.ASCII.GetString(rom,0xAC,4) != "BPEE") throw new BridgeException("此桥接适配美版底本的绿宝石。");
        if (!File.Exists(luaPath)) throw new BridgeException("应用内连接脚本缺失。");
        report("准备中","正在读取并校验游戏存档。");
        baselineBytes = File.ReadAllBytes(SavePath);
        preparation.ThrowIfCancellationRequested();
        baseline = SaveCodec.Parse(baselineBytes);
        report("准备中","正在准备本机连接与存档备份。");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            Claim(Path.Combine(stateDirectory,"bridge.lock"));
            Preserve("最新游戏存档", baselineBytes);
            var listener = new TcpListener(IPAddress.Loopback,0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            crc = Crc32(rom).ToString("x8");
            string endpointPath = Path.Combine(stateDirectory,"endpoint.json");
            File.WriteAllText(endpointPath,JsonSerializer.Serialize(Endpoint));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(endpointPath,UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string boot = Path.Combine(stateDirectory,"连接mGBA.lua");
            var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            string content = $"GBA_BRIDGE_CONFIG = {{port={port}, token=\"{token}\", crc=\"{crc}\", save_path={JsonSerializer.Serialize(SavePath,options)}, save_module={JsonSerializer.Serialize(Path.Combine(Path.GetDirectoryName(luaPath)!,"save_commit.lua"),options)}}}\ndofile({JsonSerializer.Serialize(luaPath,options)})\n";
            File.WriteAllText(boot, content, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(boot, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            ConnectionLine = "dofile(" + JsonSerializer.Serialize(boot,options) + ")";
            string backups = Path.Combine(stateDirectory,"存档备份");
            Directory.CreateDirectory(backups);
            File.WriteAllBytes(Path.Combine(backups,$"{Path.GetFileNameWithoutExtension(SavePath)}_{DateTime.Now:yyyyMMdd-HHmmss-ffffff}.sav"),baselineBytes);
        }
        catch { foreach (var file in locks) file.Dispose(); throw; }
    }

    private void Claim(string path)
    {
        var stream = new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite);
        if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(),6) != 0)
        {
            stream.Dispose();
            throw new BridgeException("已有桥接在运行，请先停止原桥接再启动内置桥接。");
        }
        locks.Add(stream);
    }

    private string Preserve(string label, byte[] data) => SaveCodec.PreserveFile(recoveryDirectory,label,data);

    internal static uint Crc32(byte[] bytes)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit=0;bit<8;bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        return ~crc;
    }

    internal void Start() => worker = Task.Run(Loop);

    private void Loop()
    {
        byte[]? pending = null;
        byte[]? blockedData = null;
        PendingEdit? submission = null;
        long pendingSince = 0;
        long lastPing = 0;
        string? lastMessage = null;
        void Publish(string state, string text)
        {
            if (lastMessage == text) return;
            lastMessage = text;
            report(state,text);
        }
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    if (client is null)
                    {
                        client = new EmulatorClient(port,token,crc);
                        Publish("已连接","mGBA 已连接，等待 PKHeX 保存。");
                    }
                    byte[] data = File.ReadAllBytes(SavePath);
                    if (submission is not null)
                    {
                        if (client.Result(submission) is int completed)
                        {
                            (baseline,baselineBytes,pending) = (submission.Updated,submission.Data,null);
                            submission = null;
                            Publish("已同步",$"事务已确认完成，已恢复游戏帧 {completed} 的提交回执。");
                            continue;
                        }
                        if (!data.SequenceEqual(submission.Data))
                        {
                            Preserve("未提交草稿",submission.Data);
                            submission = null;
                        }
                        else
                        {
                            Snapshot retrySnapshot = client.Snapshot();
                            var retryPatches = SaveCodec.Plan(baseline,submission.Updated,retrySnapshot);
                            int completedFrame = client.Apply(retrySnapshot,retryPatches,submission);
                            (baseline,baselineBytes,pending) = (submission.Updated,submission.Data,null);
                            submission = null;
                            Publish("已同步",$"事务提交已确认，游戏帧 {completedFrame}。");
                            continue;
                        }
                    }
                    if (blockedData is not null && data.SequenceEqual(blockedData))
                    {
                        cancellation.Token.WaitHandle.WaitOne(500);
                        continue;
                    }
                    if (data.SequenceEqual(baselineBytes))
                    {
                        pending = null;
                        if (Environment.TickCount64 - lastPing > 1000)
                        {
                            bool ready = client.Ping();
                            Publish(ready ? "已连接" : "等待游戏",ready ? "mGBA 已连接，等待 PKHeX 保存。" : "已连接，请在 mGBA 继续游戏或等待战斗结束。");
                            lastPing = Environment.TickCount64;
                        }
                        cancellation.Token.WaitHandle.WaitOne(300);
                        continue;
                    }
                    if (pending is null || !pending.SequenceEqual(data)) (pending,pendingSince) = (data,Environment.TickCount64);
                    if (Environment.TickCount64 - pendingSince < 300)
                    {
                        cancellation.Token.WaitHandle.WaitOne(100);
                        continue;
                    }
                    SaveData updated = SaveCodec.Parse(data,true);
                    if (SaveCodec.IsNewer(updated.Counter,baseline.Counter))
                    {
                        string latest = Preserve("最新游戏存档",data);
                        (baseline,baselineBytes,pending) = (updated,data,null);
                        Publish("等待刷新","游戏保存版本已更新，旧编辑将被阻止。最新游戏存档 " + latest);
                        continue;
                    }
                    if (updated.Counter != baseline.Counter)
                    {
                        string draft = Preserve("过期编辑草稿",data);
                        blockedData = data;
                        Publish("等待刷新","已隔离过期编辑草稿 " + draft);
                        SaveCodec.ValidateEditVersion(baseline,updated);
                    }
                    Snapshot snapshot = client.Snapshot();
                    var patches = SaveCodec.Plan(baseline,updated,snapshot);
                    if (!File.ReadAllBytes(SavePath).SequenceEqual(data)) continue;
                    if (patches.Count > 0)
                    {
                        submission = new PendingEdit(data,updated,snapshot.Session,snapshot.Epoch);
                        int frame = client.Apply(snapshot,patches,submission);
                        submission = null;
                        Publish("已同步",$"已即时同步 {patches.Count} 处数据，游戏帧 {frame}。重新打开游戏菜单可刷新显示。");
                    }
                    else Publish("已连接","存档与游戏运行数据已一致。");
                    (baseline,baselineBytes,pending) = (updated,data,null);
                    lastPing = Environment.TickCount64;
                }
                catch (StaleTransactionException error)
                {
                    if (submission is not null)
                    {
                        Preserve("时间线变化草稿",submission.Data);
                        blockedData = submission.Data;
                        submission = null;
                    }
                    Publish("等待刷新",error.Message);
                }
                catch (BridgeException error) { Publish("等待同步",error.Message); cancellation.Token.WaitHandle.WaitOne(500); }
                catch (Exception error) when (error is IOException or SocketException or TimeoutException or ObjectDisposedException or FormatException)
                {
                    client?.Dispose(); client = null;
                    if (!cancellation.IsCancellationRequested)
                        Publish("等待连接","等待 mGBA 连接，请运行面板中的连接行。");
                    cancellation.Token.WaitHandle.WaitOne(500);
                }
            }
        }
        catch (Exception error) { report("错误",error.Message); }
        finally
        {
            try
            {
                if (submission is not null) Preserve("待确认事务草稿",submission.Data);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                report("错误","待确认草稿归档失败。" + error.Message);
            }
            finally
            {
                client?.Dispose(); client = null;
                foreach (var file in locks) file.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client?.StopServer();
        cancellation.Cancel();
        client?.Dispose();
        if (worker is not null) { try { worker.Wait(2500); } catch { } }
        else foreach (var file in locks) file.Dispose();
    }
}

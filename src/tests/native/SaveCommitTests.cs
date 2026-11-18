using System.Net;
using System.Net.Sockets;
using PKHeX.GbaBridge;

internal static class SaveCommitTests
{
    internal static void Run(byte[] source)
    {
        string lease = Path.GetTempFileName();
        try
        {
            using (var owner = new FileStream(lease,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                if (!SaveCommitCoordinator.BridgeLeaseHeld(lease)) throw new Exception("Exclusive lease was not detected");
            if (SaveCommitCoordinator.BridgeLeaseHeld(lease)) throw new Exception("Released lease still appeared active");
        }
        finally { File.Delete(lease); }
        Race(source, false);
        Race(source, true);
        Console.WriteLine("PASS coordinated disk race and lost disk receipt recovery.");
    }

    private static byte[] NextGameSave(byte[] source)
    {
        byte[] result = (byte[])source.Clone();
        uint counter = SaveCodec.Parse(source).Counter + 1;
        for (int offset = 0; offset < 28 * 4096; offset += 4096)
            if (SaveCodec.U32(result,offset + 0xFF8) == 0x08012025)
                SaveCodec.Put32(result,offset + 0xFFC,counter);
        SaveCodec.Parse(result,true);
        return result;
    }

    private static void Race(byte[] source, bool loseReply)
    {
        string directory = Path.Combine(Path.GetTempPath(),"gba-coordinated-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory,"test.sav");
        File.WriteAllBytes(path,source);
        if (SaveCommitCoordinator.EmulatorRunning(path)) throw new Exception("Unrelated emulator blocked offline file");
        SaveData save = SaveCodec.Parse(source);
        byte[] newer = NextGameSave(source);
        var listener = new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        int commits = 0;
        string? receipt = null;
        var endpoint = new BridgeEndpoint(((IPEndPoint)listener.LocalEndpoint).Port,"token","crc",path);
        var server = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < (loseReply ? 2 : 1); attempt++)
            {
                using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(peer.GetStream());
                using var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true, NewLine = "\n" };
                await reader.ReadLineAsync();
                await writer.WriteLineAsync("HELLO crc test_session 0 3");
                byte[]? expected = null, desired = null;
                string? identity = null;
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    string[] fields = line.Split(' ');
                    if (fields[0] == "SNAP")
                    {
                        // The editor has completed local version validation.
                        if (!loseReply) File.WriteAllBytes(path,newer);
                        await writer.WriteLineAsync("STATE 5000 2025000 2035000 2029000 test_session 0");
                        await writer.WriteLineAsync("SMALL " + Convert.ToHexString(save.Small));
                        await writer.WriteLineAsync("LARGE " + Convert.ToHexString(save.Large));
                        await writer.WriteLineAsync("BOXES " + Convert.ToHexString(save.Boxes));
                        await writer.WriteLineAsync("PARTY " + Convert.ToHexString([save.Large[0x234],..save.Large[0x238..0x490]]));
                        await writer.WriteLineAsync("END");
                    }
                    if (fields[0] == "STATUS") await writer.WriteLineAsync(receipt ?? "UNKNOWN " + fields[1]);
                    if (fields[0] == "BEGIN") identity = fields[1];
                    if (fields[0] == "DISK")
                    {
                        expected = Convert.FromHexString(fields[1]);
                        desired = Convert.FromHexString(fields[2]);
                    }
                    if (fields[0] == "COMMIT")
                    {
                        if (expected is null || !File.ReadAllBytes(path).SequenceEqual(expected))
                            await writer.WriteLineAsync("ERROR Save conflict: disk changed after editor validation");
                        else
                        {
                            File.WriteAllBytes(path,desired!);
                            commits++;
                            receipt = $"APPLIED {identity} 0 123";
                            if (loseReply) break;
                            await writer.WriteLineAsync(receipt);
                        }
                    }
                }
            }
        });
        try
        {
            var coordinator = new SaveCommitCoordinator(Path.Combine(directory,"state"));
            bool rejected = false;
            try { coordinator.Write(path,source,endpoint,save.Counter,true); }
            catch (Exception error) when (error is IOException or BridgeException) { rejected = true; }
            if (!rejected) throw new Exception("Injected race/drop was not observed");
            if (loseReply)
            {
                File.WriteAllBytes(path,newer);
                coordinator.Write(path,source,endpoint,save.Counter,true);
                if (commits != 1) throw new Exception("Disk transaction was resubmitted");
            }
            if (!File.ReadAllBytes(path).SequenceEqual(newer)) throw new Exception("New game save was overwritten");
            server.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        finally
        {
            listener.Stop();
            Directory.Delete(directory,true);
        }
    }
}

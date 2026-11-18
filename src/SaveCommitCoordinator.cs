using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace PKHeX.GbaBridge;

internal sealed record BridgeEndpoint(int Port, string Token, string Crc, string SavePath,
    string? SocketPath = null, string? RomPath = null, string? RomSha256 = null,
    string? InstanceId = null, int Pid = 0, int Protocol = 3);
internal sealed record DiskSubmission(string Path, string Identity, byte[] Data, byte[] Expected, string Session, int Epoch,
    Snapshot? Guard = null, byte[]? LiveDraft = null);

// The emulator owns live-file replacement. The editor only submits an expected
// image and a desired image, and retains the transaction across lost replies.
internal sealed partial class SaveCommitCoordinator
{
    private readonly string directory;
    private readonly object gate = new();
    private readonly int receiveTimeout;

    internal SaveCommitCoordinator(string stateDirectory, int receiveTimeout = 6000)
    {
        directory = Path.Combine(stateDirectory,"存档备份","保存事务");
        this.receiveTimeout = receiveTimeout;
    }

    [DllImport("libSystem.B.dylib",EntryPoint="flock",SetLastError=true)]
    private static extern int Flock(int descriptor, int operation);

    internal static bool BridgeLeaseHeld(string? lease = null)
    {
        lease ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"PKHeX-Avalonia","GBABridge","bridge.lock");
        if (!File.Exists(lease)) return false;
        try
        {
            using var file = new FileStream(lease,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite);
            int descriptor = file.SafeFileHandle.DangerousGetHandle().ToInt32();
            if (Flock(descriptor,6) != 0) return true;
            Flock(descriptor,8);
            return false;
        }
        catch (IOException)
        {
            // FileStream's own Unix sharing check can detect the exclusive
            // owner before our explicit nonblocking flock is reached.
            return true;
        }
    }

    internal static bool EmulatorRunning(string path, BridgeEndpoint? endpoint = null)
    {
        var processes = Process.GetProcessesByName("mGBA");
        try
        {
            if (processes.Length == 0) return false;
            // The known owner remains authoritative during backend rebinding,
            // when the destination inode can briefly have no open descriptor.
            if (endpoint is not null && NativeDiscovery.CanonicalPath(endpoint.SavePath) == NativeDiscovery.CanonicalPath(path) && BridgeLeaseHeld()) return true;
            var probe = new ProcessStartInfo("/usr/sbin/lsof")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { "-a", "-p", string.Join(',',processes.Select(process => process.Id)), "-Fn", "--", path })
                probe.ArgumentList.Add(argument);
            using var inspection = Process.Start(probe) ?? throw new IOException("无法检查模拟器存档文件。");
            var output = inspection.StandardOutput.ReadToEndAsync();
            var errors = inspection.StandardError.ReadToEndAsync();
            if (!inspection.WaitForExit(2000)) { inspection.Kill(); return true; }
            return output.GetAwaiter().GetResult().Length > 0 || errors.GetAwaiter().GetResult().Length > 0;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal void Write(string path, byte[] data, BridgeEndpoint? endpoint, uint? knownCounter, bool emulatorRunning)
    {
        lock (gate)
        {
            try
            {
                WriteCore(NativeDiscovery.CanonicalPath(path),data,endpoint,knownCounter,emulatorRunning);
            }
            catch
            {
                PreserveDraft(path,data);
                throw;
            }
        }
    }

    internal void PreserveDraft(string path, byte[] data)
    {
        SaveCodec.PreserveFile(directory,"待处理编辑草稿",data);
        if (File.Exists(path)) SaveCodec.PreserveFile(directory,"当前磁盘存档",File.ReadAllBytes(path));
    }

    private void WriteCore(string path, byte[] data, BridgeEndpoint? endpoint, uint? knownCounter, bool emulatorRunning)
    {
        string journal = JournalPath(path);
        if (File.Exists(journal))
        {
            var pending = JsonSerializer.Deserialize<DiskSubmission>(File.ReadAllBytes(journal))
                ?? throw new IOException("保存事务记录损坏，请检查归档草稿。");
            if (endpoint is null) throw new IOException("存在待确认的保存事务，请连接 mGBA 后确认结果。");
            Submit(endpoint,pending,journal);
            if (data.AsSpan(0,0x20000).SequenceEqual(pending.Data.AsSpan(0,0x20000))) return;
        }

        byte[] expected = File.ReadAllBytes(path);
        SaveData current = SaveCodec.Parse(expected,true);
        SaveData edited = SaveCodec.Parse(data,true);
        uint latest = knownCounter ?? current.Counter;
        if (SaveCodec.IsNewer(current.Counter,latest)) latest = current.Counter;
        SaveCodec.ValidateEditVersion(current with { Counter=latest },edited);

        if (!emulatorRunning)
        {
            // Offline editing uses the normal file service. A running emulator
            // always requires the coordinated endpoint below.
            File.WriteAllBytes(path,data);
            return;
        }
        if (endpoint is null || NativeDiscovery.CanonicalPath(endpoint.SavePath) != path)
            throw new IOException("mGBA 正在运行，请为当前游戏连接新版桥接后保存。编辑草稿已保留。");

        using var client = new EmulatorClient(endpoint,receiveTimeout);
        Snapshot snapshot = client.Snapshot();
        // Keep the emulator's current RTC extension when replacing Flash data.
        byte[] desired = [..data.AsSpan(0,0x20000),..expected.AsSpan(0x20000)];
        var submission = new DiskSubmission(path,Guid.NewGuid().ToString("N"),desired,expected,snapshot.Session,snapshot.Epoch);
        WriteJournal(journal,submission);
        try { Apply(client,submission,snapshot); }
        catch (Exception error) when (error is BridgeException or StaleTransactionException)
        {
            File.Delete(journal);
            throw;
        }
        File.Delete(journal);
    }

    private void Submit(BridgeEndpoint endpoint, DiskSubmission submission, string journal, bool retain = false)
    {
        if (NativeDiscovery.CanonicalPath(endpoint.SavePath) != NativeDiscovery.CanonicalPath(submission.Path))
            throw new IOException("待确认事务属于另一份游戏存档，请恢复对应连接。");
        using var client = new EmulatorClient(endpoint,receiveTimeout);
        var pending = Pending(submission);
        try
        {
            if (client.Result(pending) is null)
            {
                if (submission.LiveDraft is not null && submission.Guard?.SaveCounter is null)
                    throw new StaleTransactionException("旧协议事务缺少保存版本保护，编辑草稿需要基于当前游戏重新提交。");
                Apply(client,submission,client.Snapshot());
            }
        }
        catch (Exception error) when (error is BridgeException or StaleTransactionException)
        {
            File.Delete(journal);
            throw;
        }
        if (!retain) File.Delete(journal);
    }

    private static PendingEdit Pending(DiskSubmission submission) => new(submission.Data,
        SaveCodec.Parse(submission.Data,true),submission.Session,submission.Epoch)
    {
        Identity = submission.Identity,
        DiskExpected = submission.Expected,
        GuardSnapshot = submission.Guard is not null,
        DiskFlashExpected = submission.Guard?.Flash
    };

    private static void Apply(EmulatorClient client, DiskSubmission submission, Snapshot snapshot)
    {
        var pending = Pending(submission);
        Snapshot guarded = submission.Guard ?? snapshot;
        var baseline = submission.Guard is null ? SaveCodec.Parse(submission.Expected,true) : SaveCodec.Effective(guarded);
        var patches = SaveCodec.Plan(baseline,pending.Updated,guarded);
        client.Apply(guarded,patches,pending);
    }

    private string JournalPath(string path)
    {
        Directory.CreateDirectory(directory);
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
        return Path.Combine(directory,key + ".json");
    }

    private static void WriteJournal(string path, DiskSubmission submission)
    {
        string temporary = path + "." + submission.Identity + ".tmp";
        try
        {
            using (var file = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(submission));
                file.Flush(true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary,UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary,path,true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

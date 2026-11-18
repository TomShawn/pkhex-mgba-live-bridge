using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PKHeX.GbaBridge;

public sealed record LiveGameInfo(string Instance, string RomPath, string SavePath);
public sealed record LiveRecentGame(string RomPath, string? Bookmark = null, string? FolderBookmark = null);
public sealed record LiveSnapshot(byte[] Image, string Timeline, int Frame);
public sealed record LiveEditResolution(byte[] Baseline, byte[] Draft, LiveSnapshot Snapshot);
public sealed record LiveRebase(byte[] Baseline, byte[] Draft, IReadOnlyList<LiveFieldConflict> Conflicts);

public sealed class LiveConflictException : IOException
{
    public IReadOnlyList<LiveFieldConflict> Conflicts { get; }
    public LiveSnapshot Snapshot { get; }
    private readonly byte[] baseline;
    private readonly byte[] draft;

    internal LiveConflictException(byte[] baseline, byte[] draft, LiveSnapshot snapshot, IReadOnlyList<LiveFieldConflict> conflicts)
        : base("游戏与修改器同时修改了相同字段，请选择本次保存采用的值。")
    {
        this.baseline = baseline;
        this.draft = draft;
        Snapshot = snapshot;
        Conflicts = conflicts;
    }

    public LiveEditResolution Resolve(bool preferDraft)
    {
        var merged = LiveGameConnection.Rebase(baseline,draft,Snapshot,preferDraft);
        return new LiveEditResolution(merged.Baseline,merged.Draft,Snapshot);
    }
}

public sealed class LiveTimelineException(string message) : IOException(message);
public sealed class LiveProtocolException(string message) : IOException(message);

// Public host boundary. Authentication material stays inside the bridge module.
public sealed class LiveGameConnection
{
    private readonly BridgeEndpoint endpoint;
    private readonly SaveCommitCoordinator coordinator;
    public LiveGameInfo Game { get; }
    public static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"PKHeX-Avalonia","GBABridge");
    public static bool SamePath(string? first, string? second) => first is not null && second is not null
        && NativeDiscovery.CanonicalPath(first) == NativeDiscovery.CanonicalPath(second);

    public static LiveRecentGame? RecentGame()
    {
        string path = Path.Combine(StateDirectory,"recent-game.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<LiveRecentGame>(File.ReadAllBytes(path)) : null; }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    public static void RememberGame(string rom, string? bookmark = null, string? folderBookmark = null)
    {
        var previous = RecentGame();
        if (SamePath(rom,previous?.RomPath))
        {
            bookmark ??= previous?.Bookmark;
            folderBookmark ??= previous?.FolderBookmark;
        }
        Directory.CreateDirectory(StateDirectory);
        string path = Path.Combine(StateDirectory,"recent-game.json");
        string stage = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(stage,JsonSerializer.SerializeToUtf8Bytes(new LiveRecentGame(rom,bookmark,folderBookmark)));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage,UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(stage,path,true);
        }
        finally { File.Delete(stage); }
    }

    public static IReadOnlyList<LiveGameInfo> Discover() => NativeDiscovery.List(StateDirectory)
        .Select(item => new LiveGameInfo(item.InstanceId!,item.RomPath!,item.SavePath)).ToArray();

    public LiveGameConnection(LiveGameInfo game)
    {
        endpoint = NativeDiscovery.List(StateDirectory,game.SavePath).SingleOrDefault(item => item.InstanceId == game.Instance)
            ?? throw new LiveTimelineException("游戏会话已结束。");
        coordinator = new SaveCommitCoordinator(StateDirectory);
        Game = game;
    }

    public LiveSnapshot Read() => ReadExpected(null);

    private LiveSnapshot ReadExpected(string? timeline)
    {
        using var client = new EmulatorClient(endpoint);
        if (timeline is not null && client.Timeline != timeline)
            throw new LiveTimelineException("游戏时间线已经变化，已接收的提交与原草稿将分别保留。");
        client.RequireLiveCapabilities();
        Snapshot state = client.Snapshot(true);
        byte[] template = state.Disk ?? throw new LiveProtocolException("模拟器没有提供当前磁盘存档。");
        return new LiveSnapshot(SaveCodec.Image(Template(state,template),SaveCodec.Effective(state)),Timeline(state),state.Frame);
    }

    public static LiveRebase Rebase(byte[] baseline, byte[] draft, LiveSnapshot current, bool? preferDraft = null)
    {
        var result = SaveCodec.Rebase(SaveCodec.Parse(baseline),SaveCodec.Parse(draft),SaveCodec.Parse(current.Image),preferDraft);
        return new LiveRebase(SaveCodec.Image(current.Image,result.Baseline),SaveCodec.Image(current.Image,result.Draft),result.Conflicts);
    }

    public static bool SameData(byte[] first, byte[] second)
    {
        SaveData a = SaveCodec.Canonical(SaveCodec.Parse(first)), b = SaveCodec.Canonical(SaveCodec.Parse(second));
        return a.Small.SequenceEqual(b.Small) && a.Large.SequenceEqual(b.Large) && a.Boxes.SequenceEqual(b.Boxes);
    }

    public LiveSnapshot Save(byte[] baseline, byte[] draft, string timeline, CancellationToken cancellation, int maxBackups = 10)
    {
        try
        {
            using var lease = coordinator.ClaimLiveSave(endpoint.SavePath,cancellation);
            coordinator.WriteLive(endpoint,baseline,draft,timeline,cancellation,maxBackups);
            LiveSnapshot receipt;
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                try { receipt = ReadExpected(timeline); break; }
                catch (LiveTimelineException) { throw; }
                catch (LiveProtocolException) { throw; }
                catch (BridgeException) { if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested(); }
                catch (Exception error) when (error is IOException or SocketException or TimeoutException)
                {
                    if (!EndpointAlive(endpoint)) throw new LiveTimelineException("模拟器会话已经结束，保存事务与草稿已保留。");
                    if (cancellation.WaitHandle.WaitOne(250)) cancellation.ThrowIfCancellationRequested();
                }
            }
            if (receipt.Timeline != timeline) throw new LiveTimelineException("提交完成后游戏时间线发生变化，已保存的事务与草稿可供核对。");
            coordinator.ConfirmLiveSave(endpoint);
            return receipt;
        }
        catch
        {
            SaveCodec.PreserveFile(Path.Combine(StateDirectory,"存档备份","实时编辑草稿"),"未确认编辑",draft);
            throw;
        }
    }

    internal static string Timeline(Snapshot snapshot) => snapshot.Session + "/" + snapshot.Epoch;
    internal static bool EndpointAlive(BridgeEndpoint endpoint) => SaveCommitCoordinator.ProcessAlive(endpoint.Pid)
        && File.Exists(Path.Combine(StateDirectory,"sessions",endpoint.InstanceId + ".json"));
    internal static byte[] Template(Snapshot state, byte[] disk)
    {
        if (state.Flash is not { Length: 0x20000 }) throw new IOException("模拟器没有提供完整的 Flash 快照。");
        if (state.SaveCounter is null) throw new LiveProtocolException("模拟器没有提供游戏保存版本，请使用配套实时版。");
        if (SaveCodec.Parse(state.Flash,true).Counter != state.SaveCounter)
            throw new BridgeException("游戏正在保存，等待完整保存后会自动继续。");
        return [..state.Flash,..disk.AsSpan(0x20000)];
    }
}

internal sealed partial class SaveCommitCoordinator
{
    [DllImport("libSystem.B.dylib",EntryPoint = "open",SetLastError = true)]
    private static extern int OpenLease([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    internal IDisposable ClaimLiveSave(string path, CancellationToken cancellation)
    {
        string leasePath = JournalPath(path) + ".lock";
        if (!File.Exists(leasePath))
        {
            try { using var initial = new FileStream(leasePath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite); }
            catch (IOException) when (File.Exists(leasePath)) { }
        }
        int descriptor = OpenLease(leasePath,2 | 0x1000000); // O_RDWR | O_CLOEXEC on macOS.
        if (descriptor < 0) throw new IOException("无法取得保存事务租约。",new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        var lease = new SafeFileHandle((nint)descriptor,true);
        try
        {
            while (Flock(descriptor,6) != 0)
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 35) throw new IOException("保存事务租约失败。",new System.ComponentModel.Win32Exception(error));
                if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal void ConfirmLiveSave(BridgeEndpoint endpoint)
    {
        string path = JournalPath(endpoint.SavePath);
        if (!File.Exists(path)) return;
        var pending = JsonSerializer.Deserialize<DiskSubmission>(File.ReadAllBytes(path));
        if (pending is not null)
        {
            try
            {
                using var client = new EmulatorClient(endpoint);
                client.Acknowledge(Pending(pending));
            }
            catch (Exception error) when (error is IOException or SocketException or TimeoutException) { }
        }
        File.Delete(path);
    }

    internal void WriteLive(BridgeEndpoint endpoint, byte[] baseline, byte[] draft, string timeline, CancellationToken cancellation, int maxBackups)
    {
        lock (gate)
        {
            string journal = JournalPath(endpoint.SavePath);
            string identity = Guid.NewGuid().ToString("N");
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(journal))
                    {
                        var pending = JsonSerializer.Deserialize<DiskSubmission>(File.ReadAllBytes(journal))
                            ?? throw new IOException("保存事务记录损坏，草稿已保留。");
                        identity = pending.Identity;
                        Submit(endpoint,pending,journal,true);
                        if (pending.LiveDraft is null || LiveGameConnection.SameData(pending.LiveDraft,draft)) return;
                        // Only changes added after the recovered edit form a new logical edit.
                        baseline = pending.LiveDraft;
                        identity = Guid.NewGuid().ToString("N");
                    }

                    using var client = new EmulatorClient(endpoint,receiveTimeout);
                    client.RequireLiveCapabilities();
                    Snapshot snapshot = client.Snapshot(true);
                    if (LiveGameConnection.Timeline(snapshot) != timeline)
                        throw new LiveTimelineException("游戏已重置、读档或切换时间线。编辑草稿已保留，请处理当前会话中的修改。");
                    byte[] expected = snapshot.Disk ?? throw new LiveProtocolException("模拟器没有提供当前磁盘存档。");
                    byte[] template = LiveGameConnection.Template(snapshot,expected);
                    SaveData active = SaveCodec.Effective(snapshot);
                    var merged = SaveCodec.Rebase(SaveCodec.Parse(baseline),SaveCodec.Parse(draft),active);
                    if (merged.Conflicts.Count != 0)
                    {
                        var live = new LiveSnapshot(SaveCodec.Image(template,active),timeline,snapshot.Frame);
                        throw new LiveConflictException(baseline,draft,live,merged.Conflicts);
                    }
                    byte[] desired = SaveCodec.Image(template,merged.Draft);
                    var submission = new DiskSubmission(endpoint.SavePath,identity,desired,expected,
                        snapshot.Session,snapshot.Epoch,snapshot,draft);
                    BackupLiveImage(endpoint.SavePath,expected,maxBackups);
                    WriteJournal(journal,submission);
                    try { Apply(client,submission,snapshot); }
                    catch (BridgeException error) when (error.Message.Contains("Snapshot changed",StringComparison.Ordinal)
                        || error.Message.Contains("Live data changed",StringComparison.Ordinal)
                        || error.Message.Contains("游戏在提交前更新了存档",StringComparison.Ordinal))
                    {
                        File.Delete(journal);
                        continue;
                    }
                    catch (Exception error) when (error is BridgeException or StaleTransactionException)
                    {
                        File.Delete(journal);
                        throw;
                    }
                    return;
                }
                catch (LiveConflictException) { throw; }
                catch (LiveTimelineException) { throw; }
                catch (LiveProtocolException) { throw; }
                catch (StaleTransactionException error) { throw new LiveTimelineException(error.Message); }
                catch (BridgeException error) when (error.Message.Contains("Snapshot changed",StringComparison.Ordinal)
                    || error.Message.Contains("Live data changed",StringComparison.Ordinal)
                    || error.Message.Contains("Game is busy",StringComparison.Ordinal)
                    || error.Message.Contains("游戏在提交前更新了存档",StringComparison.Ordinal))
                {
                    File.Delete(journal);
                    if (cancellation.WaitHandle.WaitOne(100)) cancellation.ThrowIfCancellationRequested();
                }
                catch (BridgeException error) when (error.Message.Contains("等待",StringComparison.Ordinal))
                {
                    if (cancellation.WaitHandle.WaitOne(250)) cancellation.ThrowIfCancellationRequested();
                }
                catch (Exception error) when (error is IOException or SocketException or TimeoutException)
                {
                    if (!LiveGameConnection.EndpointAlive(endpoint)) throw new LiveTimelineException("模拟器会话已结束，未确认的修改与事务记录已保留。");
                    if (cancellation.WaitHandle.WaitOne(500)) cancellation.ThrowIfCancellationRequested();
                }
            }
        }
    }

    internal static bool ProcessAlive(int id)
    {
        try { using var process = Process.GetProcessById(id); return !process.HasExited && process.ProcessName == "mGBA"; }
        catch (ArgumentException) { return false; }
    }

    private void BackupLiveImage(string path, byte[] data, int maximum)
    {
        if (maximum <= 0) return;
        try
        {
            string identity = Path.GetFileNameWithoutExtension(JournalPath(path));
            string backup = Path.Combine(directory,"提交前备份",identity);
            SaveCodec.PreserveFile(backup,"存档",data);
            foreach (string old in Directory.EnumerateFiles(backup,"*.sav").OrderByDescending(File.GetCreationTimeUtc).Skip(maximum))
                File.Delete(old);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Live save backup failed: {0}",error.Message); }
    }
}

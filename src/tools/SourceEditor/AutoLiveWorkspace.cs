using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using PKHeX.Application.Abstractions;
using PKHeX.Application.Services;
using PKHeX.Core;
using PKHeX.GbaBridge;
using PKHeX.Presentation.ViewModels;

namespace PKHeX.Avalonia.Services;

public sealed class AutoLiveWorkspace
{
    private readonly ISaveFileGateway gateway;
    private readonly IDialogService dialogs;
    private readonly BridgeSaveImageWriter writer;
    private readonly AppSettings settings;
    private readonly CancellationTokenSource lifetime = new();
    private Window main = null!;
    private MainWindowViewModel view = null!;
    private DispatcherTimer? timer;
    private LiveGameConnection? connection;
    private LiveSnapshot? baseline;
    private bool reading;
    private bool saving;
    private bool adopting;
    private bool suspended;
    private bool launchAttempted;
    private bool stopped;
    private string? selectedRom;
    private bool requestedGame;
    private IStorageFile? restoredRom;
    private IStorageFolder? restoredFolder;
    private Stream? restoredRead;
    private IAsyncEnumerator<IStorageItem>? restoredItems;

    public AutoLiveWorkspace(ISaveFileGateway gateway, IDialogService dialogs, BridgeSaveImageWriter writer, AppSettings settings)
    {
        this.gateway = gateway;
        this.dialogs = dialogs;
        this.writer = writer;
        this.settings = settings;
    }

    public void Start(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        main = window;
        view = (MainWindowViewModel)window.DataContext!;
        writer.PrepareLive = Prepare;
        BridgeModule.RefreshRequested += RefreshRequested;
        BridgeModule.GameOpened += GameOpened;
        BridgeModule.GameOpenFailed += GameOpenFailed;
        gateway.SaveFileChanged += SaveChanged;
        desktop.Exit += (_, _) => Stop();
        window.Closed += (_, _) => Stop();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += async (_, _) => await Tick();
        window.Opened += async (_, _) => { timer.Start(); await Tick(); };
        if (window.IsVisible) { timer.Start(); _ = Tick(); }
    }

    private void Stop()
    {
        if (stopped) return;
        stopped = true;
        if (view.HasLiveDraft && gateway.CurrentSave is { } save)
        {
            try { Archive(save.Write().ToArray()); }
            catch (Exception error) { Trace.TraceError("Live draft archive failed: {0}",error.Message); }
        }
        timer?.Stop();
        lifetime.Cancel();
        restoredItems?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        restoredRead?.Dispose();
        restoredRom?.Dispose();
        restoredFolder?.Dispose();
        BridgeModule.RefreshRequested -= RefreshRequested;
        BridgeModule.GameOpened -= GameOpened;
        BridgeModule.GameOpenFailed -= GameOpenFailed;
        gateway.SaveFileChanged -= SaveChanged;
    }

    private void RefreshRequested()
    {
        if (saving) return;
        if (baseline is not null && gateway.CurrentSave is { } save
            && (InputPending() || !LiveGameConnection.SameData(baseline.Image,save.Write().ToArray())))
            Archive(save.Write().ToArray());
        baseline = null;
        suspended = false;
        _ = Tick();
    }
    private void GameOpened(string path)
    {
        selectedRom = path;
        requestedGame = true;
        suspended = false;
        launchAttempted = true;
        _ = Tick();
    }
    private void GameOpenFailed(string error) { suspended = true; Status("需要处理",error); }

    private void SaveChanged(SaveFile? save)
    {
        if (adopting) return;
        if (save is null) suspended = true;
        if (connection is not null && !LiveGameConnection.SamePath(gateway.CurrentPath,connection.Game.SavePath))
        {
            connection = null;
            baseline = null;
        }
        if (save is SAV3E && baseline is null)
            baseline = new LiveSnapshot(save.Write().ToArray(),"offline",0);
    }

    private bool InputPending() => view.HasLiveDraft
        || main.IsActive && main.FocusManager?.GetFocusedElement() is TextBox { IsReadOnly: false }
        || main.OwnedWindows.Any(window => window.IsVisible && window.Title != "GBA 实时修改");

    private async Task Tick()
    {
        if (reading || saving || suspended || lifetime.IsCancellationRequested) return;
        reading = true;
        try
        {
            if (connection is not null && gateway.CurrentSave is { } local
                && !LiveGameConnection.SamePath(gateway.CurrentPath,connection.Game.SavePath))
            {
                connection = null;
                baseline = new LiveSnapshot(local.Write().ToArray(),"offline",0);
            }
            var games = await Task.Run(LiveGameConnection.Discover,lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            LiveGameInfo? selected = null;
            if (requestedGame && selectedRom is not null)
            {
                var requested = games.Where(game => LiveGameConnection.SamePath(game.RomPath,selectedRom)).ToArray();
                if (requested.Length == 0) { Status("等待游戏","所选游戏载入后会自动切换编辑会话。"); return; }
                selected = requested.Length == 1 ? requested[0] : await ChooseGame(requested);
                if (selected is null) { suspended = true; return; }
                requestedGame = false;
            }
            selected ??= games.FirstOrDefault(game => game.Instance == connection?.Game.Instance);
            selected ??= games.FirstOrDefault(game => LiveGameConnection.SamePath(game.SavePath,gateway.CurrentPath));
            if (selected is null && selectedRom is not null)
            {
                var candidates = games.Where(game => LiveGameConnection.SamePath(game.RomPath,selectedRom)).ToArray();
                if (candidates.Length == 1) selected = candidates[0];
            }
            if (selected is null && gateway.CurrentSave is not null && connection is null)
            {
                Status("离线编辑","当前存档可离线编辑。打开配套游戏后会连接对应会话。");
                return;
            }
            if (selected is null && games.Count == 1) selected = games[0];
            if (selected is null && games.Count > 1) selected = await ChooseGame(games);
            if (selected is null)
            {
                connection = null;
                if (!launchAttempted && games.Count == 0) await RestoreGame();
                if (suspended) return;
                Status("等待游戏","打开游戏后会自动读取当前人物、道具和宝可梦。");
                return;
            }
            if (connection?.Game.Instance != selected.Instance)
                connection = await Task.Run(() => new LiveGameConnection(selected),lifetime.Token);
            var active = connection;
            LiveSnapshot snapshot = await Task.Run(active.Read,lifetime.Token);
            if (lifetime.IsCancellationRequested || saving || !ReferenceEquals(connection,active)) return;
            if (baseline?.Timeline == "offline" && LiveGameConnection.SamePath(gateway.CurrentPath,active.Game.SavePath))
                baseline = baseline with { Timeline = snapshot.Timeline };
            if (baseline is null || baseline.Timeline != snapshot.Timeline || !LiveGameConnection.SamePath(gateway.CurrentPath,active.Game.SavePath))
            {
                if (baseline is not null && gateway.CurrentSave is { } previous
                    && (InputPending() || !LiveGameConnection.SameData(baseline.Image,previous.Write().ToArray())))
                {
                    string archive = Archive(previous.Write().ToArray());
                    bool refresh = await dialogs.ShowConfirmationAsync("游戏时间线已变化",
                        "旧编辑草稿已保存在\n" + archive + "\n\n读取当前游戏后可以继续编辑。",
                        "保留草稿并读取游戏","暂缓读取");
                    if (!refresh) { suspended = true; Status("需要处理","草稿已保留，可通过状态面板重新读取当前游戏。"); return; }
                }
                adopting = true;
                try { gateway.OpenLoadedSave(new SAV3E((byte[])snapshot.Image.Clone()),active.Game.SavePath); }
                finally { adopting = false; }
                baseline = snapshot;
                selectedRom = active.Game.RomPath;
                RememberGame(active.Game.RomPath);
                Status("已连接","已自动读取当前游戏。应用面板中的编辑后，点击主窗口保存即可写回游戏。");
                return;
            }
            if (gateway.CurrentSave is not SAV3E || !ReferenceEquals(view.CurrentSave,gateway.CurrentSave)) return;
            if (InputPending())
            {
                Status("编辑中","输入与待应用编辑已保留，保存时会核对最新游戏状态。");
                return;
            }
            byte[] draft = gateway.CurrentSave.Write().ToArray();
            LiveRebase merged = LiveGameConnection.Rebase(baseline.Image,draft,snapshot);
            bool edited = !LiveGameConnection.SameData(merged.Baseline,merged.Draft);
            if (!LiveGameConnection.SameData(draft,merged.Draft)) view.RefreshLiveImage(merged.Draft,edited);
            baseline = snapshot with { Image = merged.Baseline };
            Status(merged.Conflicts.Count == 0 ? "已连接" : "需要处理",merged.Conflicts.Count == 0
                ? "当前游戏数据已更新，编辑内容会在保存时自动合并。"
                : "游戏与编辑器同时修改了 " + string.Join("、",merged.Conflicts.Take(3).Select(item => item.Field)) + "，保存时请选择采用的值。");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (LiveTimelineException error) { suspended = true; Status("需要处理",error.Message); }
        catch (Exception error) { Status("等待游戏",error.Message); }
        finally { reading = false; }
    }

    private async Task RestoreGame()
    {
        launchAttempted = true;
        try
        {
            var recent = LiveGameConnection.RecentGame();
            string? rom = null;
            if (recent is not null)
            {
                rom = recent.RomPath;
                if (recent.FolderBookmark is not null)
                {
                    restoredFolder = await main.StorageProvider.OpenFolderBookmarkAsync(recent.FolderBookmark);
                    if (restoredFolder is not null)
                    {
                        // Avalonia opens the folder's security scope for the
                        // lifetime of this enumeration, not for the item itself.
                        restoredItems = restoredFolder.GetItemsAsync().GetAsyncEnumerator(lifetime.Token);
                        await restoredItems.MoveNextAsync();
                    }
                }
                if (recent.Bookmark is not null)
                {
                    restoredRom = await main.StorageProvider.OpenFileBookmarkAsync(recent.Bookmark);
                    if (restoredRom is not null)
                    {
                        restoredRead = await restoredRom.OpenReadAsync();
                        rom = restoredRom.TryGetLocalPath() ?? rom;
                    }
                }
            }
            else
            {
                string knownGame = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads",
                    "GBA口袋妖怪绿宝石386Hack版游戏合集","GBA口袋妖怪绿宝石386汉化版.gba");
                if (File.Exists(knownGame)) rom = knownGame;
                else
                {
                    string legacy = Path.Combine(LiveGameConnection.StateDirectory,"settings.json");
                    if (File.Exists(legacy))
                    {
                        using var json = JsonDocument.Parse(File.ReadAllBytes(legacy));
                        if (json.RootElement.TryGetProperty("rom",out var previous)) rom = previous.GetString();
                    }
                }
            }
            if (rom is null || !File.Exists(rom)) return;
            if (restoredRom is null)
            {
                await BridgeModule.SelectGameAsync(rom);
                return;
            }
            string emulator = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Helpers","mGBA实时版.app","Contents","MacOS","mGBA"));
            var start = new ProcessStartInfo(emulator) { UseShellExecute = false };
            start.ArgumentList.Add(rom);
            using var process = Process.Start(start);
            selectedRom = rom;
        }
        catch (Exception error) { suspended = true; Status("需要处理",error.Message); }
    }

    private static void RememberGame(string rom)
    {
        LiveGameConnection.RememberGame(rom);
    }

    private async Task<LiveGameInfo?> ChooseGame(IReadOnlyList<LiveGameInfo> games)
    {
        var window = new Window { Title = "选择当前游戏", Width = 660, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = new StackPanel { Margin = new global::Avalonia.Thickness(20), Spacing = 10 };
        foreach (var game in games)
        {
            var button = new Button { Content = Path.GetFileName(game.RomPath) + "\n" + game.SavePath, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch };
            button.Click += (_, _) => window.Close(game);
            list.Children.Add(button);
        }
        window.Content = new ScrollViewer { Content = list };
        return await window.ShowDialog<LiveGameInfo?>(main);
    }

    private IPreparedSaveWrite Prepare(string path)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return Dispatcher.UIThread.InvokeAsync(() => Prepare(path)).GetAwaiter().GetResult();
        saving = true;
        if (connection is null || baseline is null || !LiveGameConnection.SamePath(path,connection.Game.SavePath))
            return new PreparedLocalWrite(this,path);
        return new PreparedLiveWrite(this,connection,baseline);
    }

    private sealed class PreparedLocalWrite(AutoLiveWorkspace owner, string path) : IPreparedSaveWrite
    {
        public void Write(byte[] data) => BridgeModule.WriteAllBytes(path,data);
        public void Dispose()
        {
            if (Dispatcher.UIThread.CheckAccess()) owner.saving = false;
            else Dispatcher.UIThread.InvokeAsync(() => owner.saving = false).GetAwaiter().GetResult();
        }
    }

    private sealed class PreparedLiveWrite(AutoLiveWorkspace owner, LiveGameConnection connection, LiveSnapshot baseline) : IPreparedSaveWrite
    {
        public bool HandlesBackup => true;
        private byte[]? submitted;
        private LiveSnapshot? receipt;
        private Exception? failure;

        public void Write(byte[] data)
        {
            submitted = data;
            byte[] before = baseline.Image, draft = data;
            string timeline = baseline.Timeline;
            try
            {
                while (true)
                {
                    try
                    {
                        Status("正在同步","正在核对并提交编辑内容。");
                        receipt = connection.Save(before,draft,timeline,owner.lifetime.Token,
                            owner.settings.Backup.BAKEnabled ? owner.settings.SaveBackup.MaxBackupsPerSave : 0);
                        return;
                    }
                    catch (LiveConflictException conflict)
                    {
                        var operation = Dispatcher.UIThread.InvokeAsync(() => owner.ResolveConflict(conflict));
                        LiveEditResolution? resolution = operation.GetAwaiter().GetResult();
                        if (resolution is null) throw new OperationCanceledException("本次保存已取消，编辑草稿已保留。");
                        (before,draft,timeline) = (resolution.Baseline,resolution.Draft,resolution.Snapshot.Timeline);
                    }
                }
            }
            catch (Exception error) { failure = error; throw; }
        }

        public void Dispose()
        {
            void Complete()
            {
                owner.saving = false;
                if (receipt is null || submitted is null)
                {
                    if (failure is not null) Status("需要处理",failure.Message);
                    return;
                }
                if (!LiveGameConnection.SamePath(owner.gateway.CurrentPath,connection.Game.SavePath) || owner.gateway.CurrentSave is null) return;
                if (owner.InputPending())
                    owner.baseline = receipt with { Image = submitted };
                else
                {
                    byte[] current = owner.gateway.CurrentSave.Write().ToArray();
                    LiveRebase merge = LiveGameConnection.Rebase(submitted,current,receipt);
                    owner.view.RefreshLiveImage(merge.Draft,!LiveGameConnection.SameData(merge.Baseline,merge.Draft));
                    owner.baseline = receipt with { Image = merge.Baseline };
                }
                Status("已同步并保存","编辑已经在游戏中生效，存档写入已确认。");
            }
            if (Dispatcher.UIThread.CheckAccess()) Complete();
            else Dispatcher.UIThread.InvokeAsync(Complete).GetAwaiter().GetResult();
        }
    }

    private async Task<LiveEditResolution?> ResolveConflict(LiveConflictException conflict)
    {
        string text = string.Join("\n",conflict.Conflicts.Select(item =>
            $"{item.Field}\n原值 {item.Baseline}　游戏值 {item.Current}　编辑值 {item.Edited}"));
        var choice = await dialogs.ShowUnsavedChangesAsync("处理同时发生的修改",text,
            "采用编辑值","保留游戏值","取消保存");
        return choice switch
        {
            UnsavedChangesChoice.Save => conflict.Resolve(true),
            UnsavedChangesChoice.Discard => conflict.Resolve(false),
            _ => null
        };
    }

    private string Archive(byte[] image)
    {
        string directory = Path.Combine(LiveGameConnection.StateDirectory,"存档备份","实时编辑草稿");
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory,DateTime.Now.ToString("yyyyMMdd-HHmmss-ffffff"));
        File.WriteAllBytes(stem + ".sav",image);
        File.WriteAllText(stem + ".json",JsonSerializer.Serialize(new { Game = connection?.Game, Timeline = baseline?.Timeline, Pending = view.CaptureLiveDraft() }));
        return stem + ".sav";
    }

    private static void Status(string state, string text) => BridgeModule.ReportLiveStatus(state,text);
}

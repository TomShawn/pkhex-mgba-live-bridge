using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PKHeX.GbaBridge;

public static class BridgeModule
{
    private static BridgeManager? manager;
    public static event Action? RefreshRequested;
    public static event Action<string>? GameOpened;
    public static event Action<string>? GameOpenFailed;
    internal static void NotifyGameOpened(string path) => GameOpened?.Invoke(path);
    internal static void NotifyGameOpenFailed(string error) => GameOpenFailed?.Invoke(error);
    public static void RequestRefresh() => RefreshRequested?.Invoke();
    public static Task SelectGameAsync(string? suggestedPath = null) => manager?.PickNativeGame(suggestedPath) ?? Task.CompletedTask;

    public static void ManageLiveWorkspace()
    {
        if (manager is not null) manager.WorkspaceManaged = true;
    }

    public static void ReportLiveStatus(string state, string message) =>
        Dispatcher.UIThread.Post(() => manager?.Status(state,message));

    // Called by the central save service before touching the destination file.
    public static void WriteAllBytes(string path, byte[] data)
    {
        SaveWriteContext? context = null;
        try
        {
            if (manager is not null)
            {
                if (Dispatcher.UIThread.CheckAccess()) context = manager.CaptureSaveWrite(path);
                else context = Dispatcher.UIThread.InvokeAsync(() => manager.CaptureSaveWrite(path)).GetAwaiter().GetResult();
            }
        }
        catch (Exception error)
        {
            manager?.PreserveDraft(path,data);
            Dispatcher.UIThread.Post(() => manager?.SaveFailure(error.Message));
            throw;
        }
        if (context is null) File.WriteAllBytes(path,data);
        else
        {
            try
            {
                context.Coordinator.Write(path,data,context.Endpoint,context.Counter,SaveCommitCoordinator.EmulatorRunning(path,context.Endpoint));
                if (context.Endpoint?.SocketPath is not null)
                    Dispatcher.UIThread.Post(() => manager?.NativeSaveSucceeded());
            }
            catch (Exception error)
            {
                context.Coordinator.PreserveDraft(path,data);
                Dispatcher.UIThread.Post(() => manager?.SaveFailure(error.Message));
                throw new IOException(error.Message,error);
            }
        }
    }

    public static void Attach()
    {
        if (manager is not null) return;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is not { } window)
            return;
        manager = new BridgeManager(window);
        desktop.Exit += (_, _) => manager.Stop("修改器已退出。");
        window.Opened += (_, _) => Dispatcher.UIThread.Post(manager.Install, DispatcherPriority.Loaded);
        window.Closed += (_, _) => manager.Stop("修改器已关闭。");
        if (window.IsVisible) Dispatcher.UIThread.Post(manager.Install, DispatcherPriority.Loaded);
    }
}

internal sealed record SaveWriteContext(SaveCommitCoordinator Coordinator, BridgeEndpoint? Endpoint, uint? Counter);

internal sealed class BridgeManager
{
    private readonly Window main;
    private readonly string stateDirectory;
    private readonly SaveCommitCoordinator saveCoordinator;
    private BridgeWindow? panel;
    private BridgeSession? session;
    private Button? toolbar;
    private bool installed;
    private long operation;
    private CancellationTokenSource? preparation;
    private string? rememberedSave;
    private string? rememberedRom;
    private DispatcherTimer? nativeTimer;
    private bool nativeProbeRunning;
    private long nativeSavedAt;
    private IStorageFile? grantedRom;
    private IStorageFolder? grantedFolder;
    internal string State { get; private set; } = "未启动";
    internal string Message { get; private set; } = "打开当前游戏存档后，可在此启动实时桥接。";
    internal string ConnectionLine => session?.ConnectionLine ?? string.Empty;
    internal bool IsRunning => session is not null;
    internal bool IsStarting { get; private set; }
    internal bool WorkspaceManaged { get; set; }
    internal string EmbeddedEmulatorPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Helpers","mGBA实时版.app"));
    internal bool HasEmbeddedEmulator => Directory.Exists(EmbeddedEmulatorPath);
    internal event Action? Updated;
    internal List<string> Logs { get; } = [];

    internal BridgeManager(Window main)
    {
        this.main = main;
        stateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"PKHeX-Avalonia","GBABridge");
        saveCoordinator = new SaveCommitCoordinator(stateDirectory);
        if (main.DataContext is INotifyPropertyChanged observable)
            observable.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is not ("CurrentSave" or "CurrentSavePath")) return;
                if (IsStarting || session is not null && !string.Equals(SavePath,session.SavePath,StringComparison.Ordinal))
                    Stop("存档已切换，桥接已停止。请为新存档重新启动。");
                Updated?.Invoke();
            };
        try
        {
            var settings = JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(stateDirectory,"settings.json")));
            rememberedSave = settings?.GetValueOrDefault("save");
            rememberedRom = settings?.GetValueOrDefault("rom");
        }
        catch (Exception error) when (error is IOException or JsonException) { }
    }

    internal string? SavePath => main.DataContext?.GetType().GetProperty("CurrentSavePath")?.GetValue(main.DataContext) is string path
        ? NativeDiscovery.CanonicalPath(path) : null;
    internal bool SupportedSave => main.DataContext?.GetType().GetProperty("CurrentSave")?.GetValue(main.DataContext)?.GetType().Name == "SAV3E";

    internal string SuggestedRom
    {
        get
        {
            if (SavePath is not { Length: > 0 } save) return string.Empty;
            string paired = Path.ChangeExtension(save,".gba");
            if (File.Exists(paired)) return paired;
            return rememberedSave == save && File.Exists(rememberedRom) ? rememberedRom! : string.Empty;
        }
    }

    internal SaveWriteContext? CaptureSaveWrite(string path)
    {
        path = NativeDiscovery.CanonicalPath(path);
        BridgeEndpoint? endpoint = NativeDiscovery.Find(stateDirectory,path) ?? session?.Endpoint;
        if (endpoint is null)
        {
            try { endpoint = JsonSerializer.Deserialize<BridgeEndpoint>(File.ReadAllText(Path.Combine(stateDirectory,"endpoint.json"))); }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
        bool currentGame = SupportedSave && path == SavePath;
        bool connectedGame = endpoint is not null && Path.GetFullPath(endpoint.SavePath) == path;
        if (!currentGame && !connectedGame) return null;
        return new SaveWriteContext(saveCoordinator,endpoint,session?.SavePath == path ? session.LatestCounter : null);
    }

    internal void SaveFailure(string message)
    {
        Status("等待处理",message + " 草稿与事务记录位于 " + Path.Combine(stateDirectory,"存档备份","保存事务"));
        Open();
    }

    internal void NativeSaveSucceeded()
    {
        nativeSavedAt = Environment.TickCount64;
        Status("已同步并保存","已通过配套模拟器完成运行数据与磁盘存档的协调提交。");
    }

    internal void PreserveDraft(string path, byte[] data) => saveCoordinator.PreserveDraft(path,data);

    internal void Install()
    {
        if (installed) return;
        var menu = main.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
        if (menu is null) return;
        var top = menu.GetVisualAncestors().OfType<Grid>().FirstOrDefault(grid => grid.Children.OfType<StackPanel>().Any(stack => Grid.GetColumn(stack) == 1));
        var stack = top?.Children.OfType<StackPanel>().FirstOrDefault(candidate => Grid.GetColumn(candidate) == 1);
        if (stack is not null)
        {
            toolbar = new Button { Content = HasEmbeddedEmulator ? "游戏 · 等待连接" : "实时桥接 · 未启动", Padding = new Thickness(8,3), FontSize = 11 };
            AutomationProperties.SetName(toolbar,HasEmbeddedEmulator ? "实时游戏状态" : "实时桥接");
            toolbar.Click += (_, _) => Open();
            stack.Children.Insert(0,toolbar);
            if (HasEmbeddedEmulator)
            {
                var openGame = new Button { Content = "打开游戏…", Padding = new Thickness(8,3), FontSize = 11 };
                openGame.Click += async (_, _) => await PickNativeGame();
                stack.Children.Insert(0,openGame);
            }
        }
        else
        {
            var item = new MenuItem { Header = "实时桥接" };
            var manage = new MenuItem { Header = "管理 GBA 实时桥接…" };
            manage.Click += (_, _) => Open();
            item.Items.Add(manage);
            menu.Items.Add(item);
        }
        installed = true;
        if (HasEmbeddedEmulator && !WorkspaceManaged)
        {
            nativeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            nativeTimer.Tick += async (_, _) => await ProbeNative();
            nativeTimer.Start();
            _ = ProbeNative();
        }
    }

    private async Task ProbeNative()
    {
        if (nativeProbeRunning || nativeTimer?.IsEnabled != true || State == "等待处理") return;
        if (SavePath is not { Length: > 0 } path || !SupportedSave)
        {
            Status("等待存档","打开游戏后，请在主窗口打开对应的 .sav 存档。配套模拟器会自动连接。");
            return;
        }
        nativeProbeRunning = true;
        try
        {
            var endpoint = await Task.Run(() => NativeDiscovery.Find(stateDirectory,path));
            if (SavePath != path || nativeTimer?.IsEnabled != true) return;
            if (endpoint is null) Status("等待游戏","通过此面板打开游戏，配套模拟器会自动建立本机连接。");
            else if (Environment.TickCount64 - nativeSavedAt > 5000)
                Status("已连接","已自动连接配套模拟器。主窗口保存会协调写入当前游戏和存档。");
        }
        catch (Exception error) { Status("等待处理",error.Message); }
        finally { nativeProbeRunning = false; }
    }

    internal async Task LaunchNativeGame(string path, IStorageFile? selectedFile = null)
    {
        try
        {
            string digest = await Task.Run(() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant());
            if (digest != "af821cf6f017b41241f2be73fe9bba34a1e411fdc3da235e333bd0890b9b3195")
                throw new BridgeException("当前实时版适配本机已验证的绿宝石 386 汉化 ROM，请选择该游戏文件。");
            if (selectedFile is not null)
            {
                grantedRom?.Dispose();
                grantedFolder?.Dispose();
                grantedRom = selectedFile;
                grantedFolder = await selectedFile.GetParentAsync();
                string? bookmark = selectedFile.CanBookmark ? await selectedFile.SaveBookmarkAsync() : null;
                string? folder = grantedFolder?.CanBookmark == true ? await grantedFolder.SaveBookmarkAsync() : null;
                LiveGameConnection.RememberGame(Path.GetFullPath(path),bookmark,folder);
            }
            var running = await Task.Run(() => NativeDiscovery.List(stateDirectory)
                .Where(game => LiveGameConnection.SamePath(game.RomPath,path)).ToArray());
            if (running.Length == 1)
            {
                using var client = new EmulatorClient(running[0]);
                client.RequireLiveCapabilities();
                BridgeModule.NotifyGameOpened(Path.GetFullPath(path));
                Status("已连接","已连接正在运行的所选游戏。");
                return;
            }
            string paired = Path.ChangeExtension(path,".sav");
            if (File.Exists(paired) && await Task.Run(() => SaveCommitCoordinator.EmulatorRunning(paired)))
                throw new BridgeException("这份存档已有运行中的模拟器。请先在原游戏中保存并关闭窗口，再打开实时版。");
            var launch = new ProcessStartInfo(Path.Combine(EmbeddedEmulatorPath,"Contents","MacOS","mGBA")) { UseShellExecute = false };
            launch.ArgumentList.Add(path);
            using var process = Process.Start(launch) ?? throw new IOException("无法启动配套模拟器。");
            LiveGameConnection.RememberGame(Path.GetFullPath(path));
            BridgeModule.NotifyGameOpened(Path.GetFullPath(path));
            Status("等待游戏","配套模拟器已启动，连接将在游戏载入后自动建立。");
        }
        catch (Exception error) { Status("等待处理",error.Message); BridgeModule.NotifyGameOpenFailed(error.Message); }
    }

    internal async Task PickNativeGame(string? suggestedPath = null)
    {
        var folder = suggestedPath is null ? null
            : await main.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetDirectoryName(suggestedPath)! + Path.DirectorySeparatorChar));
        var files = await main.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 GBA 游戏", AllowMultiple = false,
            SuggestedStartLocation = folder,
            FileTypeFilter = [new FilePickerFileType("GBA 游戏") { Patterns = ["*.gba"] }]
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is string path) await LaunchNativeGame(path,files[0]);
    }

    private void Open()
    {
        if (panel is not null) { panel.Activate(); return; }
        panel = new BridgeWindow(this,main);
        panel.Closed += (_, _) => panel = null;
        panel.Show(main);
    }

    internal void Status(string state, string message)
    {
        if (State == state && Message == message) return;
        State = state;
        Message = message;
        Logs.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (Logs.Count > 60) Logs.RemoveAt(0);
        if (toolbar is not null)
        {
            toolbar.Content = (HasEmbeddedEmulator ? "游戏 · " : "实时桥接 · ") + state;
            ToolTip.SetTip(toolbar,message);
        }
        Updated?.Invoke();
    }

    internal async Task Start(string rom)
    {
        if (session is not null || IsStarting) return;
        long currentOperation = ++operation;
        var preparing = new CancellationTokenSource();
        preparation = preparing;
        IsStarting = true;
        Updated?.Invoke();
        try
        {
            if (SavePath is not { Length: > 0 } save || !SupportedSave)
                throw new BridgeException("请先在主窗口打开当前绿宝石游戏的 .sav 存档。");
            if (string.IsNullOrWhiteSpace(rom))
                throw new BridgeException("请选择当前游戏的 .gba ROM 文件。");
            string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Resources","GBABridge","mgba_bridge.lua"));
            var started = await Task.Run(() => new BridgeSession(rom,save,script,stateDirectory,(state,message) => Dispatcher.UIThread.Post(() =>
            {
                if (operation == currentOperation) Status(state,message);
            }),preparing.Token));
            if (operation != currentOperation) { started.Dispose(); return; }
            session = started;
            rememberedRom = started.RomPath;
            rememberedSave = save;
            File.WriteAllText(Path.Combine(stateDirectory,"settings.json"),JsonSerializer.Serialize(new Dictionary<string,string>{{"rom",rememberedRom},{"save",save}}));
            Status("等待连接","已启动。请在 mGBA 继续游戏，并运行下面的连接行一次。");
            started.Start();
            if (Process.GetProcessesByName("mGBA").Length == 0)
            {
                var launch = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                launch.ArgumentList.Add("-a");
                launch.ArgumentList.Add("/Applications/mGBA.app");
                launch.ArgumentList.Add(started.RomPath);
                Process.Start(launch);
            }
        }
        catch (Exception error)
        {
            if (operation == currentOperation)
            {
                session?.Dispose(); session = null;
                Status("错误",error.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(preparation,preparing)) preparation = null;
            preparing.Dispose();
            if (operation == currentOperation)
            {
                IsStarting = false;
                Updated?.Invoke();
            }
        }
    }

    internal void Stop(string message = "实时桥接已停止。")
    {
        grantedRom?.Dispose(); grantedRom = null;
        grantedFolder?.Dispose(); grantedFolder = null;
        nativeTimer?.Stop();
        var previous = session;
        session = null;
        preparation?.Cancel();
        IsStarting = false;
        operation++;
        previous?.Dispose();
        Status("已停止",message);
    }
}

internal sealed class BridgeWindow : Window
{
    private readonly BridgeManager manager;
    private readonly TextBox save = new() { IsReadOnly = true };
    private readonly TextBox rom = new();
    private readonly TextBox connection = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 65 };
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 130 };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button browse = new() { Content = "选择 ROM…" };
    private readonly Button start = new() { Content = "启动桥接" };
    private readonly Button stop = new() { Content = "停止桥接" };
    private readonly Button copy = new() { Content = "复制连接行" };
    private string? displayedSave;
    private IStorageFile? selectedRom;

    internal BridgeWindow(BridgeManager manager, Window main)
    {
        this.manager = manager;
        Title = manager.HasEmbeddedEmulator ? "GBA 实时修改" : "GBA 实时桥接";
        Width = 730; Height = 620; MinWidth = 620; MinHeight = 580;
        Background = main.Background;
        Foreground = main.Foreground;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(rom); Grid.SetColumn(browse,1); row.Children.Add(browse);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (!manager.HasEmbeddedEmulator)
        {
            buttons.Children.Add(start); buttons.Children.Add(stop); buttons.Children.Add(copy);
        }
        else
        {
            var refresh = new Button { Content = "重新读取当前游戏" };
            refresh.Click += (_, _) => BridgeModule.RequestRefresh();
            buttons.Children.Add(refresh);
        }
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 9 };
        content.Children.Add(new TextBlock { Text = "当前存档" });
        content.Children.Add(save);
        content.Children.Add(new TextBlock { Text = "游戏 ROM" });
        content.Children.Add(row);
        content.Children.Add(buttons);
        content.Children.Add(status);
        if (!manager.HasEmbeddedEmulator)
        {
            content.Children.Add(new TextBlock { Text = "在 mGBA「工具 → 脚本」运行下面的连接行一次。之后返回游戏，在 PKHeX 点击保存即可自动同步。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(connection);
        }
        content.Children.Add(new TextBlock { Text = "同步记录" });
        content.Children.Add(log);
        Content = new ScrollViewer { Content = content };
        AutomationProperties.SetName(save,"当前桥接存档");
        AutomationProperties.SetName(rom,"桥接游戏 ROM");
        AutomationProperties.SetName(connection,"mGBA 连接行");
        AutomationProperties.SetName(log,"桥接同步记录");
        AutomationProperties.SetName(status,"桥接状态");
        if (manager.HasEmbeddedEmulator) { browse.Content = "打开游戏…"; rom.IsReadOnly = true; }
        browse.Click += async (_, _) =>
        {
            if (await SelectRom() && manager.HasEmbeddedEmulator)
                await manager.LaunchNativeGame(rom.Text ?? string.Empty,selectedRom);
        };
        start.Click += async (_, _) =>
        {
            // The native picker grants access to this file under macOS privacy rules.
            if (selectedRom is null || selectedRom.TryGetLocalPath() != rom.Text)
                if (!await SelectRom()) return;
            await manager.Start(rom.Text ?? string.Empty);
        };
        stop.Click += (_, _) => manager.Stop();
        copy.Click += async (_, _) =>
        {
            if (Clipboard is not null && !string.IsNullOrEmpty(connection.Text))
                await Clipboard.SetTextAsync(connection.Text);
        };
        manager.Updated += Refresh;
        Closed += (_, _) => manager.Updated -= Refresh;
        Refresh();
    }

    private async Task<bool> SelectRom()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择并允许读取当前游戏 ROM", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("GBA 游戏") { Patterns = ["*.gba"] }]
        });
        if (files.Count == 0) return false;
        selectedRom = files[0];
        rom.Text = selectedRom.TryGetLocalPath();
        return true;
    }

    private void Refresh()
    {
        string? path = manager.SavePath;
        if (path != displayedSave)
        {
            displayedSave = path;
            selectedRom = null;
            save.Text = path ?? "请先在主窗口打开 .sav 存档";
            rom.Text = manager.SuggestedRom;
        }
        start.IsEnabled = !manager.IsRunning && !manager.IsStarting && manager.SupportedSave && !string.IsNullOrEmpty(path);
        stop.IsEnabled = manager.IsRunning || manager.IsStarting;
        rom.IsEnabled = !manager.IsRunning && !manager.IsStarting;
        browse.IsEnabled = rom.IsEnabled;
        copy.IsEnabled = manager.IsRunning;
        connection.Text = manager.ConnectionLine;
        status.Text = manager.Message;
        log.Text = string.Join(Environment.NewLine,manager.Logs);
    }
}

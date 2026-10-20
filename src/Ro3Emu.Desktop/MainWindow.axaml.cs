using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Ro3Emu.Desktop;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(Engine.AppDir, LauncherSettings.FileName);
    private readonly LauncherSettings _settings;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? _statusCheck;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _settings = LauncherSettings.Load(_settingsPath);
        ServerList.ItemsSource = _settings.Servers;
        ServerList.SelectedItem = _settings.Servers.FirstOrDefault(s => s.Address == _settings.Selected) ?? _settings.Servers[0];
        AccountBox.Text = _settings.Account;
        var saved = _settings.ReadPassword();
        PasswordBox.Text = saved;
        RememberBox.IsChecked = saved != null;
        ClientBox.Text = _settings.ClientDir;

        ServerList.SelectionChanged += (_, _) => { Save(); _ = RefreshStatusAsync(); };
        AddButton.Click += AddServer;
        RemoveButton.Click += RemoveServer;
        UpdateButton.Click += async (_, _) => await UpdateDllsAsync();
        PlayButton.Click += async (_, _) => await PlayAsync();
        BrowseButton.Click += async (_, _) => await BrowseAsync();
        _statusTimer.Tick += (_, _) => _ = RefreshStatusAsync();
        _statusTimer.Start();
        Opened += (_, _) =>
        {
            _ = RefreshStatusAsync();
            if (Program.UnderWine()) Log("Running under Wine.");
        };
        Closing += (_, _) => Save();
    }

    private ServerEntry? Selected => ServerList.SelectedItem as ServerEntry;

    private void AddServer(object? sender, RoutedEventArgs e)
    {
        var address = NewAddress.Text?.Trim() ?? "";
        if (Engine.ServerUrl(address) == null) { Log($"'{address}' is not a server address (use host, host:port or http://host:port)."); return; }
        var entry = new ServerEntry { Name = NewName.Text?.Trim() ?? "", Address = address };
        _settings.Servers.Add(entry);
        RefreshList(entry);
        NewName.Text = NewAddress.Text = "";
    }

    private void RemoveServer(object? sender, RoutedEventArgs e)
    {
        if (Selected is not { } server || _settings.Servers.Count <= 1) return;
        _settings.Servers.Remove(server);
        RefreshList(_settings.Servers[0]);
    }

    private void RefreshList(ServerEntry select)
    {
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = _settings.Servers;
        ServerList.SelectedItem = select;
        Save();
    }

    private void Save()
    {
        _settings.Selected = Selected?.Address;
        _settings.Account = AccountBox.Text?.Trim();
        _settings.ClientDir = ClientBox.Text?.Trim();
        _settings.WritePassword(RememberBox.IsChecked == true ? PasswordBox.Text : null);
        try { _settings.Save(_settingsPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("Settings not saved: " + e.Message); }
    }

    private async Task RefreshStatusAsync()
    {
        if (Selected is not { } server) return;
        _statusCheck?.Cancel();
        var cts = _statusCheck = new CancellationTokenSource();
        var players = await Engine.StatusAsync(server.Address, cts.Token);
        if (cts.IsCancellationRequested) return;
        StatusDot.Fill = new SolidColorBrush(Color.Parse(players != null ? "#22C55E" : "#EF4444"));
        StatusText.Text = players switch
        {
            null => $"{server.Address} does not answer",
            1 => $"{server.Address} is online, 1 player",
            _ => $"{server.Address} is online, {players} players",
        };
    }

    private async Task UpdateDllsAsync()
    {
        if (!Begin()) return;
        try
        {
            Log("Updating the client DLLs from the newest CI build...");
            var commit = await Engine.UpdateClientDllsAsync(Log, CancellationToken.None);
            Log($"Client DLLs are the build of {commit}. They go into the game on the next Play.");
        }
        catch (Exception e) { Log("Update failed: " + e.Message); }
        finally { End(); }
    }

    private async Task PlayAsync()
    {
        if (Selected is not { } server) { Log("Choose a server first."); return; }
        var account = AccountBox.Text?.Trim();
        var password = PasswordBox.Text;
        if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password))
        {
            Log("Enter your account and password, so the game can skip its login screen.");
            return;
        }
        if (!Begin()) return;
        Save();
        try
        {
            await CheckDllsAsync();
            Log($"Starting {server}...");
            var failure = await Engine.PlayAsync(server.Address, account, password, ClientBox.Text, Log);
            Log(failure == null ? "Done. Have fun!" : "Stopped: " + failure);
        }
        catch (Exception e) { Log("Could not start: " + e.Message); }
        finally { End(); }
    }

    /// <summary>Before Play: get the newest CI build of the client DLLs if there is one; carry on with the current ones if GitHub can't be reached.</summary>
    private async Task CheckDllsAsync()
    {
        try
        {
            var (commit, updated) = await Engine.UpdateIfNewerAsync(Log, CancellationToken.None);
            Log(updated ? $"Client DLLs updated to the build of {commit}." : $"Client DLLs are up to date ({commit}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log("Could not check for newer client DLLs (" + e.Message + "); playing with the current ones.");
        }
    }

    private async Task BrowseAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Ragnarok 3 game folder (the one with ro3.exe)",
            AllowMultiple = false,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            ClientBox.Text = path;
            if (!File.Exists(Path.Combine(path, "ro3.exe"))) Log($"{path} has no ro3.exe; pick the folder that contains it.");
            Save();
        }
    }

    private bool Begin()
    {
        if (_busy) return false;
        _busy = true;
        PlayButton.IsEnabled = UpdateButton.IsEnabled = false;
        return true;
    }

    private void End()
    {
        _busy = false;
        PlayButton.IsEnabled = UpdateButton.IsEnabled = true;
    }

    private void Log(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Log(line)); return; }
        LogBox.Text = (LogBox.Text ?? "") + line + Environment.NewLine;
        LogBox.CaretIndex = LogBox.Text.Length;
    }
}

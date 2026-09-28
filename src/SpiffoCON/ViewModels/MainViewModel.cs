using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;
using SpiffoCON.Core.Rcon;
using SpiffoCON.Services;

namespace SpiffoCON.ViewModels;

public enum ConsoleKind { Command, Reply, Info, Warning, Error }

public sealed record ConsoleLine(DateTime Time, ConsoleKind Kind, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed record ColorPreset(string Name, RgbColor Color)
{
    public string Hex
    {
        get
        {
            var (r, g, b) = Color.ToBytes();
            return $"#{r:X2}{g:X2}{b:X2}";
        }
    }
}

public sealed partial class MainViewModel : ObservableObject
{
    const int MaxConsoleLines = 5000;

    readonly RconClient _rcon = new();
    readonly ServerProfile _profile;
    readonly List<string> _history = [];
    int _historyIndex;

    public MainViewModel()
    {
        _profile = ProfileStore.Load();
        Host = _profile.Host;
        RconPort = _profile.RconPort;
        RememberPasswords = _profile.RememberPasswords;
        SftpEnabled = _profile.SftpEnabled;
        SftpPort = _profile.SftpPort;
        SftpUser = _profile.SftpUser;
        SftpSamePassword = _profile.SftpSamePassword;
        RconPassword = ProfileStore.Unprotect(_profile.RconPassword);
        SftpPassword = ProfileStore.Unprotect(_profile.SftpPassword);

        _rcon.ConnectionLost += (_, error) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (IsSessionActive)
                Log(ConsoleKind.Warning, "Connection lost" + (error is null ? "" : $" ({error.Message})")
                    + ". It will reconnect with the next command.");
            StatusText = "Connection lost: reconnects with the next command";
        });

        UpdateMessagePreview();
        Catalog = new CatalogViewModel(this);
    }

    public CatalogViewModel Catalog { get; }

    internal ServerProfile Profile => _profile;

    /// <summary>SFTP settings when SFTP is enabled and filled in, else null.</summary>
    internal SftpSettings? CurrentSftpSettings()
    {
        var password = SftpSamePassword ? RconPassword : SftpPassword;
        if (!SftpEnabled || string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(SftpUser) || password.Length == 0)
            return null;
        return new SftpSettings(Host.Trim(), SftpPort, SftpUser.Trim(), password, _profile.SftpHostKey);
    }

    // ---- connection ----

    [ObservableProperty] private string host = "";
    [ObservableProperty] private int rconPort;
    [ObservableProperty] private bool rememberPasswords;

    /// <summary>Set from the PasswordBox by the view (PasswordBox can't bind).</summary>
    public string RconPassword { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditConnection))]
    private bool isSessionActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditConnection))]
    private bool isBusy;

    [ObservableProperty] private string statusText = "Not connected";

    public bool CanEditConnection => !IsSessionActive && !IsBusy;

    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = [];

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(Host) || RconPort is <= 0 or > 65535 || RconPassword.Length == 0)
        {
            StatusText = "Enter host, RCON port and password.";
            return;
        }

        IsBusy = true;
        StatusText = $"Connecting to {Host.Trim()}:{RconPort}...";
        try
        {
            await _rcon.ConnectAsync(Host.Trim(), RconPort, RconPassword);
            IsSessionActive = true;
            StatusText = $"Connected to {Host.Trim()}:{RconPort}";
            Log(ConsoleKind.Info, StatusText);
            _ = Catalog.RefreshPlayersCommand.ExecuteAsync(null);
            SaveProfile();
        }
        catch (RconException ex)
        {
            StatusText = ex.Message;
            Log(ConsoleKind.Error, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _rcon.DisconnectAsync();
        IsSessionActive = false;
        StatusText = "Disconnected";
        Log(ConsoleKind.Info, "Disconnected");
    }

    // ---- console ----

    [ObservableProperty] private string consoleInput = "";

    [RelayCommand]
    private async Task SendConsoleAsync()
    {
        var command = ConsoleInput.Trim();
        if (command.Length == 0 || !IsSessionActive)
            return;
        // the in-game console wants "/players", RCON wants "players"
        if (command.StartsWith('/'))
            command = command[1..];

        _history.Remove(command);
        _history.Add(command);
        _historyIndex = _history.Count;
        ConsoleInput = "";
        await RunAsync(command);
    }

    /// <summary>Up/down arrows in the console input.</summary>
    public void BrowseHistory(int direction)
    {
        if (_history.Count == 0)
            return;
        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        ConsoleInput = _historyIndex < _history.Count ? _history[_historyIndex] : "";
    }

    [RelayCommand]
    private void ClearConsole() => ConsoleLines.Clear();

    /// <summary>Runs a command, logging it and its reply; null when it failed (the error is logged).</summary>
    internal async Task<string?> RunAsync(string command, bool logReply = true)
    {
        Log(ConsoleKind.Command, "> " + command);
        try
        {
            var reply = await _rcon.ExecuteAsync(command);
            StatusText = $"Connected to {Host.Trim()}:{RconPort}";
            if (logReply)
                Log(ConsoleKind.Reply, reply.Length == 0 ? "(empty reply)" : reply.TrimEnd());
            else
                Log(ConsoleKind.Info, $"({reply.ReplaceLineEndings("\n").Split('\n').Length} lines received)");
            return reply;
        }
        catch (RconException ex)
        {
            Log(ex is RconTimeoutException ? ConsoleKind.Warning : ConsoleKind.Error, ex.Message);
            if (ex is RconAuthenticationException)
            {
                IsSessionActive = false;
                StatusText = "Disconnected: the password was rejected";
            }
            return null;
        }
    }

    void Log(ConsoleKind kind, string text)
    {
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            ConsoleLines.Add(new ConsoleLine(DateTime.Now, kind, line));
        while (ConsoleLines.Count > MaxConsoleLines)
            ConsoleLines.RemoveAt(0);
    }

    // ---- broadcast message ----

    public IReadOnlyList<ColorPreset> ColorPresets { get; } =
    [
        new("White", new RgbColor(1f, 1f, 1f)),
        new("Red", new RgbColor(1f, 0.2f, 0.2f)),
        new("Orange", new RgbColor(1f, 0.6f, 0f)),
        new("Yellow", new RgbColor(1f, 0.9f, 0.2f)),
        new("Green", new RgbColor(0.3f, 0.9f, 0.3f)),
        new("Cyan", new RgbColor(0.2f, 0.9f, 1f)),
        new("Blue", new RgbColor(0.4f, 0.6f, 1f)),
        new("Magenta", new RgbColor(1f, 0.4f, 1f)),
    ];

    [ObservableProperty] private string messageText = "";
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<MessageSegment>> messagePreview = [];
    [ObservableProperty] private string messageInfo = "";
    [ObservableProperty] private string messageResult = "";

    partial void OnMessageTextChanged(string value) => UpdateMessagePreview();

    void UpdateMessagePreview()
    {
        MessagePreview = ServerMessage.Preview(MessageText, RgbColor.White);
        int bytes = _rcon.Options.Encoding.GetByteCount(ServerMessage.BuildCommand(MessageText));
        MessageInfo = $"{bytes} / {_rcon.Options.MaxCommandBytes} bytes";
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (!IsSessionActive || ServerMessage.ToMarkup(MessageText).Length == 0)
            return;
        var reply = await RunAsync(ServerMessage.BuildCommand(MessageText));
        MessageResult = reply is null
            ? "Not sent: see the console."
            : ServerMessage.Interpret(reply) switch
            {
                CommandOutcome.Success => $"Sent at {DateTime.Now:HH:mm:ss}.",
                CommandOutcome.Failed => $"The server refused it: {reply.Trim()}",
                _ => reply.Trim().Length == 0
                    ? "Sent; the server gave no confirmation."
                    : $"Sent; unexpected reply: {reply.Trim()}",
            };
    }

    // ---- SFTP ----

    [ObservableProperty] private bool sftpEnabled;
    [ObservableProperty] private int sftpPort;
    [ObservableProperty] private string sftpUser = "";
    [ObservableProperty] private bool sftpSamePassword;
    [ObservableProperty] private string sftpReport = "Not tested yet. Enable SFTP on the left and press Test.";

    public string SftpPassword { get; set; }

    /// <summary>Asks the user a yes/no question; set by the view.</summary>
    public Func<string, bool>? Confirm { get; set; }

    [RelayCommand]
    private async Task TestSftpAsync()
    {
        var password = SftpSamePassword ? RconPassword : SftpPassword;
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(SftpUser) || password.Length == 0)
        {
            SftpReport = "Enter host, SFTP user and password.";
            return;
        }

        IsBusy = true;
        SftpReport = $"Connecting to {Host.Trim()}:{SftpPort} as {SftpUser.Trim()}...";
        try
        {
            SftpProbeResult result;
            try
            {
                result = await SftpProbe.RunAsync(new SftpSettings(Host.Trim(), SftpPort, SftpUser.Trim(), password, _profile.SftpHostKey));
            }
            catch (SftpHostKeyMismatchException ex)
            {
                if (Confirm?.Invoke(ex.Message + "\n\nTrust the new key?") != true)
                {
                    SftpReport = ex.Message;
                    return;
                }
                result = await SftpProbe.RunAsync(new SftpSettings(Host.Trim(), SftpPort, SftpUser.Trim(), password, null));
            }

            _profile.SftpHostKey = result.HostKeyFingerprint;
            _profile.SftpWorkshopFolder = result.WorkshopFolders.OrderByDescending(w => w.ItemCount).FirstOrDefault()?.Path;
            SaveProfile();
            SftpReport = FormatReport(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SftpReport = "SFTP failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    static string FormatReport(SftpProbeResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Connected.");
        sb.AppendLine($"Host key: {r.HostKeyFingerprint}");
        sb.AppendLine($"Start folder: {r.StartDirectory}");
        sb.AppendLine();
        sb.AppendLine("Top-level entries:");
        foreach (var e in r.TopLevel)
            sb.AppendLine("  " + e);
        sb.AppendLine();
        Section("Server configs (Server/*.ini)", r.ServerConfigs);
        Section("Workshop mod folders (content/108600)", r.WorkshopFolders.Select(w => $"{w.Path}  ({w.ItemCount} items)").ToList());
        Section("Local mod folders (Zomboid/mods)", r.ModFolders);
        Section("Log folders", r.LogFolders);
        sb.AppendLine($"Scanned {r.ScannedDirectories} folders{(r.Truncated ? " (stopped at the limit)" : "")}.");
        sb.AppendLine(r.WorkshopFolders.Count > 0
            ? "Mod files are reachable over SFTP."
            : "No workshop folder found: mods will come from your Steam folder or SteamCMD.");
        return sb.ToString();

        void Section(string title, IReadOnlyList<string> items)
        {
            sb.AppendLine(title + ":");
            if (items.Count == 0)
                sb.AppendLine("  (none found)");
            foreach (var i in items)
                sb.AppendLine("  " + i);
            sb.AppendLine();
        }
    }

    // ---- profile ----

    public void SaveProfile()
    {
        _profile.Host = Host.Trim();
        _profile.RconPort = RconPort;
        _profile.RememberPasswords = RememberPasswords;
        _profile.RconPassword = RememberPasswords ? ProfileStore.Protect(RconPassword) : null;
        _profile.SftpEnabled = SftpEnabled;
        _profile.SftpPort = SftpPort;
        _profile.SftpUser = SftpUser.Trim();
        _profile.SftpSamePassword = SftpSamePassword;
        _profile.SftpPassword = RememberPasswords && !SftpSamePassword ? ProfileStore.Protect(SftpPassword) : null;
        try
        {
            ProfileStore.Save(_profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Could not save the profile: " + ex.Message;
        }
    }

    public async Task ShutdownAsync()
    {
        SaveProfile();
        await _rcon.DisposeAsync();
    }
}

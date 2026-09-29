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

public enum NotificationKind { Players, Chat, Connection, Test }

/// <summary>A desktop notification; <paramref name="OnlyWhenInactive"/> skips it while SpiffoCON is the active window.</summary>
public sealed record AppNotification(NotificationKind Kind, string Title, string Text, bool OnlyWhenInactive);

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
    readonly ServerBook _book;
    readonly ServerProfile _profile;
    readonly List<string> _history = [];
    int _historyIndex;

    /// <summary>Works on the book's selected server; switching server means a new view model.</summary>
    public MainViewModel(ServerBook book)
    {
        _book = book;
        _profile = book.Current;
        _loadingProfile = true;
        ServerName = _profile.Name;
        Host = _profile.Host;
        RconPort = _profile.RconPort;
        RememberPasswords = _profile.RememberPasswords;
        SftpEnabled = _profile.SftpEnabled;
        SftpCustomHost = _profile.SftpCustomHost;
        SftpHost = _profile.SftpHost;
        SftpPort = _profile.SftpPort;
        SftpUser = _profile.SftpUser;
        SftpSamePassword = _profile.SftpSamePassword;
        // passwords typed earlier in this session count too (they may not be remembered on disk)
        _book.SessionPasswords.TryGetValue(_profile.Id, out var typed);
        RconPassword = ProfileStore.Unprotect(_profile.RconPassword) is { Length: > 0 } rcon ? rcon : typed.Rcon ?? "";
        SftpPassword = ProfileStore.Unprotect(_profile.SftpPassword) is { Length: > 0 } sftp ? sftp : typed.Sftp ?? "";
        _loadingProfile = false;

        _rcon.ConnectionLost += (_, error) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (IsSessionActive)
            {
                Log(ConsoleKind.Warning, "Connection lost" + (error is null ? "" : $" ({error.Message})")
                    + ". It will reconnect with the next command.");
                if (!_connectionLost && !_expectingShutdown)
                    Notify(NotificationKind.Connection, "Connection lost", $"{_profile.DisplayName}: the server stopped answering.");
                _connectionLost = true;
                StatusText = "Connection lost: reconnects with the next command";
            }
        });

        var notifications = _book.Notifications;
        NotifyPlayerJoins = notifications.PlayerJoins;
        NotifyChat = notifications.Chat;
        NotifyChatWords = notifications.ChatWords;
        NotifyConnection = notifications.Connection;
        NotifyOnlyWhenInactive = notifications.OnlyWhenInactive;

        UpdateMessagePreview();
        Catalog = new CatalogViewModel(this);
        Kits = new KitsViewModel(this);
        Players = new PlayersViewModel(this);
        Options = new OptionsViewModel(this);
        Sandbox = new SandboxViewModel(this);
        Logs = new LogsViewModel(this);
        Logs.ChatReceived += (_, lines) => OnChat(lines);
        Bridge = new BridgeViewModel(this);
        Map = new MapViewModel(this);
        Accounts = new AccountsViewModel(this);
        Events = new EventsViewModel(this);
        Maintenance = new MaintenanceViewModel(this);
    }

    public KitsViewModel Kits { get; }

    public MaintenanceViewModel Maintenance { get; }

    public AccountsViewModel Accounts { get; }

    public EventsViewModel Events { get; }

    public BridgeViewModel Bridge { get; }

    public MapViewModel Map { get; }

    public LogsViewModel Logs { get; }

    public SandboxViewModel Sandbox { get; }

    public CatalogViewModel Catalog { get; }

    public OptionsViewModel Options { get; }

    public PlayersViewModel Players { get; }

    internal ServerProfile Profile => _profile;

    /// <summary>The game folder chosen for the Map tab, shared by all servers.</summary>
    internal string? MapFolder
    {
        get => _book.MapFolder;
        set
        {
            _book.MapFolder = value;
            SaveProfile();
        }
    }

    /// <summary>The SFTP address: the server's host, unless another one is set.</summary>
    internal string SftpHostName => (SftpCustomHost && !string.IsNullOrWhiteSpace(SftpHost) ? SftpHost : Host).Trim();

    /// <summary>SFTP settings when SFTP is enabled and filled in, else null.</summary>
    /// <param name="trustKey">The key to require instead of the saved one (a change the user accepted).</param>
    internal SftpSettings? CurrentSftpSettings(string? trustKey = null)
    {
        var password = SftpSamePassword ? RconPassword : SftpPassword;
        if (!SftpEnabled || SftpHostName.Length == 0 || string.IsNullOrWhiteSpace(SftpUser) || password.Length == 0)
            return null;
        var host = SftpHostName;
        var dispatcher = Application.Current?.Dispatcher;
        return new SftpSettings(host, SftpPort, SftpUser.Trim(), password, trustKey ?? SavedHostKey(host))
        {
            // whichever feature connects first to a host with no saved key, that key is kept
            OnFirstKey = fingerprint => dispatcher?.BeginInvoke(() =>
            {
                if (_shutDown || SavedHostKey(host) is not null)
                    return;
                RememberSftpHostKey(host, fingerprint);
                SaveProfile();
            }),
        };
    }

    /// <summary>A saved host key only counts for the host it came from.</summary>
    string? SavedHostKey(string host) =>
        (_profile.SftpHostKeyFor ?? _profile.Host).Equals(host, StringComparison.OrdinalIgnoreCase) ? _profile.SftpHostKey : null;

    /// <summary>
    /// Runs the SFTP probe and keeps the host key it saw: the first SFTP use (whichever tab it
    /// comes from) is when the key is trusted; later connections check against it.
    /// </summary>
    internal async Task<SftpProbeResult> ProbeAsync(SftpSettings sftp, CancellationToken ct = default)
    {
        var probe = await SftpProbe.RunAsync(sftp, ct);
        if (!_shutDown)
        {
            RememberSftpHostKey(sftp.Host, probe.HostKeyFingerprint);
            _profile.SftpFoldersHost = FoldersKey(sftp);
            SaveProfile();
        }
        return probe;
    }

    /// <summary>Saves the SSH host key just seen, for the current SFTP host.</summary>
    internal void RememberSftpHostKey(string host, string fingerprint)
    {
        _profile.SftpHostKey = fingerprint;
        _profile.SftpHostKeyFor = host;
    }

    bool _loadingProfile;

    partial void OnHostChanged(string value) => SftpTargetChanged();
    partial void OnSftpHostChanged(string value) => SftpTargetChanged();
    partial void OnSftpCustomHostChanged(bool value) => SftpTargetChanged();
    partial void OnSftpPortChanged(int value) => SftpTargetChanged();
    partial void OnSftpUserChanged(string value) => SftpTargetChanged();

    /// <summary>Which account on which host the remembered folders were found with.</summary>
    static string FoldersKey(SftpSettings sftp) => $"{sftp.Host}|{sftp.Port}|{sftp.User}";

    /// <summary>Turning "remember" off takes the saved passwords out of the file at once.</summary>
    partial void OnRememberPasswordsChanged(bool value)
    {
        if (!_loadingProfile && _profile is not null)
            SaveProfile();
    }

    /// <summary>
    /// Folders found on one SFTP host, port or account mean nothing on another (on the same host,
    /// another server may live under another user): forget them.
    /// </summary>
    void SftpTargetChanged()
    {
        if (_loadingProfile || _profile is null)
            return;
        var target = $"{SftpHostName}|{SftpPort}|{SftpUser.Trim()}";
        var known = _profile.SftpFoldersHost ?? _profile.Host;
        // before 0.9.6 only the host was recorded
        if (known.Equals(target, StringComparison.OrdinalIgnoreCase)
            || (!known.Contains('|') && known.Equals(SftpHostName, StringComparison.OrdinalIgnoreCase)))
            return;
        _profile.SftpWorkshopFolder = null;
        _profile.SftpLogsFolder = null;
        _profile.SftpLuaFolder = null;
        _profile.SftpZomboidFolder = null;
        _profile.SftpSandboxPath = null;
        _profile.SftpFoldersHost = target;
    }

    // ---- server list ----

    public ObservableCollection<ServerProfile> Servers => _book.Servers;

    /// <summary>Picking another server asks the window to switch (only while disconnected).</summary>
    public ServerProfile SelectedServer
    {
        get => _profile;
        set
        {
            if (value is null || value == _profile)
                return;
            if (!CanEditConnection || !ConfirmLeaving("Switch to another server"))
            {
                OnPropertyChanged();
                return;
            }
            SwitchRequested?.Invoke(this, value);
        }
    }

    /// <summary>Raised to work on another server of the list; the window builds a new view model for it.</summary>
    public event EventHandler<ServerProfile>? SwitchRequested;

    [ObservableProperty] private string serverName = "";

    partial void OnServerNameChanged(string value)
    {
        if (_profile is not null)
            _profile.Name = value;
    }

    [RelayCommand]
    private void AddServer()
    {
        if (!ConfirmLeaving("Switch to a new server"))
            return;
        SaveProfile();
        var server = new ServerProfile { Name = UniqueName("New server") };
        Servers.Add(server);
        SwitchRequested?.Invoke(this, server);
    }

    [RelayCommand]
    private void DuplicateServer()
    {
        if (!ConfirmLeaving("Switch to the copy"))
            return;
        SaveProfile();
        var copy = _profile.Duplicate(UniqueName(_profile.DisplayName + " (copy)"));
        _book.SessionPasswords[copy.Id] = (RconPassword, SftpPassword);
        Servers.Insert(Servers.IndexOf(_profile) + 1, copy);
        SwitchRequested?.Invoke(this, copy);
    }

    [RelayCommand]
    private void DeleteServer()
    {
        if (!ConfirmLeaving("Leave this server"))
            return;
        if (Confirm?.Invoke($"Remove \"{_profile.DisplayName}\" from the server list?\n\n"
                + "Its settings and saved passwords are deleted. The server itself is not touched.") != true)
            return;
        int index = Servers.IndexOf(_profile);
        Servers.Remove(_profile);
        _book.SessionPasswords.Remove(_profile.Id);
        if (Servers.Count == 0)
            Servers.Add(new ServerProfile { Name = "My server" });
        SwitchRequested?.Invoke(this, Servers[Math.Clamp(index, 0, Servers.Count - 1)]);
    }

    string UniqueName(string name)
    {
        var candidate = name;
        for (int n = 2; Servers.Any(s => s.DisplayName.Equals(candidate, StringComparison.CurrentCultureIgnoreCase)); n++)
            candidate = $"{name} {n}";
        return candidate;
    }

    // ---- work that would be lost ----

    /// <summary>What disconnecting, switching or closing now would drop (empty: nothing).</summary>
    internal List<string> PendingWork()
    {
        var pending = new List<string>();
        if (Options.ChangedCount > 0)
            pending.Add($"{Options.ChangedCount} server option change(s) not applied");
        if (Sandbox.ChangedCount > 0)
            pending.Add($"{Sandbox.ChangedCount} sandbox change(s) not saved");
        if (Maintenance.IsCountingDown)
            pending.Add("the restart countdown (it is called off and the players are told)");
        if (Events.RainTimerRunning)
            pending.Add("the rain timer (the rain won't stop by itself)");
        return pending;
    }

    /// <summary>Asks before <paramref name="action"/> drops pending work; true when there is none or the user agrees.</summary>
    internal bool ConfirmLeaving(string action)
    {
        var pending = PendingWork();
        return pending.Count == 0
            || Confirm?.Invoke($"{action} now? This drops:\n\n" + string.Join("\n", pending.Select(p => "• " + p))) == true;
    }

    // ---- notifications ----

    bool _connectionLost;
    bool _expectingShutdown;
    HashSet<string>? _knownPlayers;

    /// <summary>Raised for a desktop notification; the window decides whether to show it.</summary>
    public event EventHandler<AppNotification>? NotificationRaised;

    [ObservableProperty] private bool notifyPlayerJoins;
    [ObservableProperty] private bool notifyChat;
    [ObservableProperty] private string notifyChatWords = "";
    [ObservableProperty] private bool notifyConnection;
    [ObservableProperty] private bool notifyOnlyWhenInactive;

    partial void OnNotifyPlayerJoinsChanged(bool value) => _book.Notifications.PlayerJoins = value;
    partial void OnNotifyChatChanged(bool value) => _book.Notifications.Chat = value;
    partial void OnNotifyChatWordsChanged(string value) => _book.Notifications.ChatWords = value;
    partial void OnNotifyConnectionChanged(bool value) => _book.Notifications.Connection = value;
    partial void OnNotifyOnlyWhenInactiveChanged(bool value) => _book.Notifications.OnlyWhenInactive = value;

    internal void Notify(NotificationKind kind, string title, string text)
    {
        // an old server's view model (after a switch) may still finish a poll
        if (_shutDown)
            return;
        bool wanted = kind switch
        {
            NotificationKind.Players => NotifyPlayerJoins,
            NotificationKind.Chat => NotifyChat,
            NotificationKind.Connection => NotifyConnection,
            _ => true,
        };
        if (wanted)
            NotificationRaised?.Invoke(this, new AppNotification(kind, title, text, NotifyOnlyWhenInactive && kind != NotificationKind.Test));
    }

    [RelayCommand]
    private void TestNotification() =>
        Notify(NotificationKind.Test, "SpiffoCON", "Notifications work. This is how a player joining or a chat message shows up.");

    /// <summary>The next shutdown is ours (restart): no "connection lost" alarm.</summary>
    internal void ExpectShutdown()
    {
        // not "lost" yet: quit still answers before the server goes away
        _expectingShutdown = true;
        _connectionLost = false;
        // everyone leaves with the restart: the list after it is a new baseline, not "X left"
        _knownPlayers = null;
    }

    /// <summary>The shutdown did not happen (or the server never came back): alarms as usual again.</summary>
    internal void EndExpectedShutdown() => _expectingShutdown = false;

    /// <summary>The server went away after the shutdown SpiffoCON asked for (and is not back yet).</summary>
    internal bool ConnectionLostSinceShutdown => _connectionLost;

    /// <summary>New chat lines from the Logs tab; a burst becomes one notification.</summary>
    void OnChat(IReadOnlyList<SpiffoCON.Core.Files.LogLine> lines)
    {
        var words = NotifyChatWords.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var wanted = lines.Where(l => words.Length == 0 || words.Any(w => l.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (wanted.Count is > 0 and <= 3)
            foreach (var l in wanted)
                Notify(NotificationKind.Chat, $"{l.Author ?? "?"} ({_profile.DisplayName})", l.Text);
        else if (wanted.Count > 3)
            Notify(NotificationKind.Chat, $"{wanted.Count} chat messages ({_profile.DisplayName})",
                $"Latest: {wanted[^1].Author ?? "?"}: {wanted[^1].Text}");
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
            _knownPlayers = null;
            _connectionLost = _expectingShutdown = false;
            IsSessionActive = true;
            StatusText = $"Connected to {Host.Trim()}:{RconPort}";
            Log(ConsoleKind.Info, StatusText);
            await RefreshPlayersAsync();
            await Options.RefreshCommand.ExecuteAsync(null);
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
        if (!ConfirmLeaving("Disconnect"))
            return;
        // the players were warned: tell them it is off before the connection goes
        if (Maintenance.IsCountingDown)
            await Maintenance.CancelRestartCommand.ExecuteAsync(null);
        await _rcon.DisconnectAsync();
        _knownPlayers = null;
        IsSessionActive = false;
        OnlinePlayers.Clear();
        OnlinePlayersChanged?.Invoke(this, EventArgs.Empty);
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
            command = command[1..].Trim();
        if (command.Length == 0)
            return;

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

    /// <summary>
    /// Runs a command, logging it and its reply; null when it failed (the error is logged).
    /// <paramref name="quiet"/> logs only failures (background polling).
    /// </summary>
    internal async Task<string?> RunAsync(string command, bool logReply = true, bool quiet = false)
    {
        if (!quiet)
            Log(ConsoleKind.Command, "> " + command);
        try
        {
            var reply = await _rcon.ExecuteAsync(command);
            _lastQuietFailure = null;
            StatusText = $"Connected to {Host.Trim()}:{RconPort}";
            // quit's own reply comes before the server goes away: it is not a return
            if (_connectionLost && command != "quit")
            {
                _connectionLost = false;
                Log(ConsoleKind.Info, "Connected again.");
                Notify(NotificationKind.Connection, "Server back", $"{_profile.DisplayName} answers again.");
                if (_expectingShutdown)
                {
                    _expectingShutdown = false;
                    // the list after the restart is a new baseline, whatever was polled while it went down
                    _knownPlayers = null;
                    ServerBack?.Invoke(this, EventArgs.Empty);
                }
            }
            if (quiet)
                return reply;
            if (logReply)
                Log(ConsoleKind.Reply, reply.Length == 0 ? "(empty reply)" : reply.TrimEnd());
            else
                Log(ConsoleKind.Info, $"({reply.ReplaceLineEndings("\n").Split('\n').Length} lines received)");
            return reply;
        }
        catch (RconException ex)
        {
            // a background poll failing the same way every 30 s (server down for hours) is logged once
            var line = (quiet ? command + ": " : "") + ex.Message;
            if (!quiet || line != _lastQuietFailure)
                Log(ex is RconTimeoutException ? ConsoleKind.Warning : ConsoleKind.Error, line);
            _lastQuietFailure = quiet ? line : null;
            // after our own quit a failing command means it is down, even if no drop was seen
            if (_expectingShutdown && command != "quit")
                _connectionLost = true;
            // a server shutting down or starting up may drop logins: not a wrong password
            if (ex is RconAuthenticationException && !_expectingShutdown)
            {
                IsSessionActive = false;
                StatusText = "Disconnected: the password was rejected";
            }
            return null;
        }
        catch (ObjectDisposedException)
        {
            // the window is closing (or the server was switched) while a command was on its way
            return null;
        }
    }

    // ---- online players (shared by the Catalog and Players tabs) ----

    public ObservableCollection<string> OnlinePlayers { get; } = [];

    /// <summary>Raised after <see cref="OnlinePlayers"/> was refreshed.</summary>
    public event EventHandler? OnlinePlayersChanged;

    /// <summary>Raised when the server answers again after a shutdown SpiffoCON asked for.</summary>
    public event EventHandler? ServerBack;

    bool _refreshingPlayers;
    string? _lastQuietFailure;

    internal async Task RefreshPlayersAsync(bool quiet = false)
    {
        if (!IsSessionActive)
            return;
        // background refreshes (players tab, restart wait) don't pile up in the command queue
        if (quiet && _refreshingPlayers)
            return;
        _refreshingPlayers = true;
        string? reply;
        try
        {
            reply = await RunAsync("players", quiet: quiet);
        }
        finally
        {
            _refreshingPlayers = false;
        }
        if (reply is null)
            return;
        // one row per name, in a stable order also for names equal but for case
        var names = PlayerCommands.ParsePlayers(reply).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.CurrentCultureIgnoreCase).ThenBy(n => n, StringComparer.Ordinal).ToList();
        NotifyJoins(names);
        SetOnlinePlayers(names);
    }

    /// <summary>
    /// Updates the list in place: a Clear() would reset every list and combo box bound to it,
    /// and they would lose (or write back an empty) choice of player.
    /// </summary>
    internal void SetOnlinePlayers(IReadOnlyList<string> sortedNames)
    {
        if (sortedNames.SequenceEqual(OnlinePlayers))
            return;
        var keep = sortedNames.ToHashSet(StringComparer.Ordinal);
        for (int i = OnlinePlayers.Count - 1; i >= 0; i--)
            if (!keep.Contains(OnlinePlayers[i]))
                OnlinePlayers.RemoveAt(i);
        // what is left is in the same order as the new list: insert the newcomers in place
        for (int i = 0; i < sortedNames.Count; i++)
            if (i >= OnlinePlayers.Count || OnlinePlayers[i] != sortedNames[i])
                OnlinePlayers.Insert(i, sortedNames[i]);
        // rows left over (a name listed twice before) go, so the list is exactly the new one
        while (OnlinePlayers.Count > sortedNames.Count)
            OnlinePlayers.RemoveAt(OnlinePlayers.Count - 1);
        OnlinePlayersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Joins and leaves since the last refresh (the first list after connecting is the baseline).</summary>
    void NotifyJoins(IReadOnlyList<string> names)
    {
        var now = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_knownPlayers is { } before)
        {
            var joined = now.Where(n => !before.Contains(n)).ToList();
            var left = before.Where(n => !now.Contains(n)).ToList();
            if (joined.Count > 0)
                Notify(NotificationKind.Players, $"{string.Join(", ", joined)} joined", $"{_profile.DisplayName}: {now.Count} online.");
            if (left.Count > 0)
                Notify(NotificationKind.Players, $"{string.Join(", ", left)} left", $"{_profile.DisplayName}: {now.Count} online.");
        }
        _knownPlayers = now;
    }

    /// <summary>
    /// The server's workshop/content/108600 folder: the one remembered from the SFTP probe, or
    /// found now (probing takes a moment). Null without SFTP or when there is none.
    /// </summary>
    internal async Task<string?> FindWorkshopFolderAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_profile.SftpWorkshopFolder is { } known)
            return known;
        if (CurrentSftpSettings() is not { } sftp)
            return null;
        progress?.Report("Looking for the mod folder over SFTP...");
        var probe = await SftpProbe.RunAsync(sftp, ct);
        var folder = probe.WorkshopFolders.OrderByDescending(w => w.ItemCount).FirstOrDefault()?.Path;
        if (_shutDown)
            return folder;
        RememberSftpHostKey(sftp.Host, probe.HostKeyFingerprint);
        _profile.SftpFoldersHost = FoldersKey(sftp);
        _profile.SftpWorkshopFolder = folder;
        SaveProfile();
        return folder;
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
    [ObservableProperty] private bool sftpCustomHost;
    [ObservableProperty] private string sftpHost = "";
    [ObservableProperty] private string sftpReport = "Not tested yet. Enable SFTP on the left and press Test.";

    public string SftpPassword { get; set; }

    /// <summary>Asks the user a yes/no question; set by the view.</summary>
    public Func<string, bool>? Confirm { get; set; }

    [RelayCommand]
    private async Task TestSftpAsync()
    {
        var settings = CurrentSftpSettings();
        if (settings is null)
        {
            SftpReport = "Enter the host (or the SFTP host), SFTP user and password.";
            return;
        }

        IsBusy = true;
        SftpReport = $"Connecting to {settings.Host}:{settings.Port} as {settings.User}...";
        try
        {
            SftpProbeResult result;
            try
            {
                result = await SftpProbe.RunAsync(settings);
            }
            catch (SftpHostKeyMismatchException ex)
            {
                if (Confirm?.Invoke(ex.Message + "\n\nTrust the new key?") != true)
                {
                    SftpReport = ex.Message;
                    return;
                }
                // exactly the key the user just saw and accepted, not whatever comes next
                result = await SftpProbe.RunAsync(CurrentSftpSettings(trustKey: ex.Actual)!);
            }

            RememberSftpHostKey(settings.Host, result.HostKeyFingerprint);
            _profile.SftpFoldersHost = FoldersKey(settings);
            _profile.SftpWorkshopFolder = result.WorkshopFolders.OrderByDescending(w => w.ItemCount).FirstOrDefault()?.Path;
            SaveProfile();
            SftpReport = FormatReport(result);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SftpReport = $"SFTP failed on {settings.Host}:{settings.Port}: {ex.Message}";
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

    /// <summary>Set once the view model is shut down (server switch, close): it must not touch the book any more.</summary>
    bool _shutDown;

    public void SaveProfile()
    {
        // a scan still running for the server just left must not overwrite the list
        if (_shutDown)
            return;
        _profile.Host = Host.Trim();
        _profile.RconPort = RconPort;
        _profile.RememberPasswords = RememberPasswords;
        _profile.RconPassword = RememberPasswords ? ProfileStore.Protect(RconPassword) : null;
        _profile.SftpEnabled = SftpEnabled;
        _profile.SftpCustomHost = SftpCustomHost;
        _profile.SftpHost = SftpHost.Trim();
        _profile.SftpPort = SftpPort;
        _profile.SftpUser = SftpUser.Trim();
        _profile.SftpSamePassword = SftpSamePassword;
        _profile.SftpPassword = RememberPasswords && !SftpSamePassword ? ProfileStore.Protect(SftpPassword) : null;
        // a deleted server is saved one last time on the way out: leave the book alone then
        if (_book.Servers.Contains(_profile))
        {
            _book.SessionPasswords[_profile.Id] = (RconPassword, SftpPassword);
            _book.Selected = _profile.Id;
        }
        try
        {
            ProfileStore.Save(_book);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Could not save the server list: " + ex.Message;
        }
    }

    public async Task ShutdownAsync()
    {
        SaveProfile();
        _shutDown = true;
        Catalog.Close();
        Players.Close();
        Logs.Close();
        Map.Close();
        Bridge.Close();
        Events.Close();
        Maintenance.Close();
        await _rcon.DisposeAsync();
    }
}

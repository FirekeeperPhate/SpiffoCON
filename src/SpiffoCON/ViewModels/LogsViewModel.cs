using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;

namespace SpiffoCON.ViewModels;

public sealed record LogLineItem(LogLine Line)
{
    public string TimeText => Line.Time is { } t ? (t.Date == DateTime.Today ? t.ToString("HH:mm:ss") : t.ToString("dd/MM HH:mm")) : "";
    public LogLineKind Kind => Line.Kind;
    public string? Channel => Line.Channel is { Length: > 0 } c ? c : null;
    public string? Author => Line.Kind == LogLineKind.Alert ? "Broadcast" : Line.Author;
    public string Text => Line.Text;

    // formatted here: a WPF StringFormat would still print "[] " for a missing value
    public string ChannelText => Channel is null ? "" : $"[{Channel}] ";
    public string AuthorText => Author is null ? "" : Author + ": ";
}

/// <summary>
/// Server logs read over SFTP (or from a local folder) and followed while they grow: the chat
/// (RCON can't see it), joins, admin actions and the rest of Zomboid/Logs.
/// </summary>
public sealed partial class LogsViewModel : ObservableObject
{
    const int MaxLines = 5000;

    readonly MainViewModel _main;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly ObservableCollection<LogLineItem> _lines = [];
    ILogFolder? _folder;
    LogTail? _tail;
    bool _polling;

    public LogsViewModel(MainViewModel main)
    {
        _main = main;
        View = new ListCollectionView(_lines) { Filter = Matches };
        _timer.Tick += async (_, _) => await PollAsync();
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            View.Refresh();
        };
    }

    /// <summary>Asks for a local Logs folder; set by the view.</summary>
    public Func<string?>? PickFolder { get; set; }

    /// <summary>A chat message written since the log was opened (not the older lines read at opening).</summary>
    public event EventHandler<IReadOnlyList<LogLine>>? ChatReceived;

    public ICollectionView View { get; }

    public ObservableCollection<string> Types { get; } = [];

    [ObservableProperty] private string? selectedType;
    [ObservableProperty] private bool follow = true;
    [ObservableProperty] private bool showServerLines;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string source = "No log folder.";
    [ObservableProperty] private string statusText = "Read the server's logs over SFTP (Find on server), or open a local Logs folder.";
    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private string message = "";

    public bool IsChat => string.Equals(SelectedType, "chat", StringComparison.OrdinalIgnoreCase);

    bool _settingType;
    int _pollCount;

    partial void OnSelectedTypeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsChat));
        View.Refresh();
        if (_folder is not null && value is not null && !_settingType)
            _ = StartTailAsync(value);
    }

    partial void OnFollowChanged(bool value) => UpdateTimer();
    partial void OnShowServerLinesChanged(bool value) => View.Refresh();

    partial void OnSearchChanged(string value)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    void UpdateTimer()
    {
        if (Follow && _tail is not null && !_closed)
            _timer.Start();
        else
            _timer.Stop();
    }

    bool Matches(object o)
    {
        var item = (LogLineItem)o;
        if (IsChat && !ShowServerLines && item.Kind == LogLineKind.Server)
            return false;
        var s = Search.Trim();
        return s.Length == 0
            || item.Text.Contains(s, StringComparison.CurrentCultureIgnoreCase)
            || item.Author?.Contains(s, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    // ---- opening ----

    [RelayCommand]
    private async Task FindOnServerAsync()
    {
        var sftp = _main.CurrentSftpSettings();
        if (sftp is null)
        {
            StatusText = "Enable SFTP on the left (user and password) to read the server's logs.";
            return;
        }
        var path = _main.Profile.SftpLogsFolder;
        bool remembered = path is not null;
        if (path is null)
        {
            StatusText = "Looking for the Logs folder over SFTP...";
            try
            {
                var probe = await _main.ProbeAsync(sftp);
                // from now on the key this probe saw is required
                sftp = _main.CurrentSftpSettings() ?? sftp;
                path = probe.LogFolders.OrderBy(p => p.Contains("/Zomboid/", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Length).FirstOrDefault();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                StatusText = "SFTP: " + ex.Message;
                return;
            }
            if (path is null)
            {
                StatusText = "No Logs folder found over SFTP.";
                return;
            }
            _main.Profile.SftpLogsFolder = path;
            _main.SaveProfile();
        }
        if (!await OpenAsync(new SftpLogFolder(sftp, path)) && remembered && !_closed
            && _openError is Renci.SshNet.Common.SftpPathNotFoundException)
        {
            // the folder remembered from last time is gone or wrong: look for it again once
            _main.Profile.SftpLogsFolder = null;
            await FindOnServerAsync();
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (PickFolder?.Invoke() is { } path)
            await OpenAsync(new LocalLogFolder(path));
    }

    /// <summary>Opens a Logs folder and follows its newest log; false when it can't be read.</summary>
    async Task<bool> OpenAsync(ILogFolder folder)
    {
        StatusText = "Reading " + folder.Description + "...";
        try
        {
            var files = await folder.ListAsync();
            if (_closed)
            {
                // the server was switched meanwhile: this folder is not for anyone any more
                folder.Dispose();
                return false;
            }
            _folder?.Dispose();
            _folder = folder;
            Source = folder.Description;
            IsOpen = true;

            var keep = SelectedType ?? "chat";
            UpdateTypes(files);
            if (Types.Count == 0)
            {
                StatusText = "No log files in this folder.";
                return true;
            }
            _settingType = true;
            SelectedType = Types.FirstOrDefault(t => t.Equals(keep, StringComparison.OrdinalIgnoreCase)) ?? Types[0];
            _settingType = false;
            await StartTailAsync(SelectedType);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            folder.Dispose();
            _openError = ex;
            StatusText = "Could not read the logs: " + ex.Message;
            return false;
        }
    }

    /// <summary>Chat first, then the logs admins read most, then the rest. Keeps the selection.</summary>
    void UpdateTypes(IReadOnlyList<LogFileInfo> files)
    {
        var order = new[] { "chat", "user", "admin", "pvp", "cmd", "item", "map", "PerkLog", "DebugLog-server" };
        var types = files.Select(f => f.Type).Distinct(StringComparer.OrdinalIgnoreCase)
            .Append(SelectedType ?? "")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => Array.FindIndex(order, o => o.Equals(t, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : order.Length)
            .ThenBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (types.SequenceEqual(Types))
            return;
        var keep = SelectedType;
        _settingType = true;
        Types.Clear();
        foreach (var t in types)
            Types.Add(t);
        SelectedType = keep;
        _settingType = false;
    }

    async Task StartTailAsync(string type)
    {
        if (_folder is null)
            return;
        _tail = new LogTail(_folder, type);
        _lines.Clear();
        await PollAsync();
        UpdateTimer();
    }

    // ---- following ----

    [RelayCommand]
    private Task RefreshAsync() => PollAsync();

    [RelayCommand]
    private void Clear() => _lines.Clear();

    async Task PollAsync()
    {
        if (_tail is null || _polling || DateTime.UtcNow < _retryAt)
            return;
        _polling = true;
        var tail = _tail;
        try
        {
            using var timeout = new CancellationTokenSource(PollTimeout);
            var lines = await tail.PollAsync(timeout.Token);
            _failures = 0;
            // the type changed meanwhile: these lines are not for the list (the new one is read below)
            if (ReferenceEquals(tail, _tail))
            {
                Show(tail, lines);
                // new kinds of log appear as the server writes them (admin, pvp...): look every 30 s
                if (_folder is not null && (++_pollCount % 6 == 0 || lines.Any(l => l.Kind == LogLineKind.Marker)))
                    UpdateTypes(await _folder.ListAsync(timeout.Token));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a poll of a log (or folder) that was replaced meanwhile failing is not news
            if (!ReferenceEquals(tail, _tail))
            {
            }
            else if (ex is Renci.SshNet.Common.SshAuthenticationException or SftpHostKeyMismatchException)
            {
                // retrying a rejected password every few seconds can get the PC banned by the host
                _timer.Stop();
                StatusText = $"Stopped following: {ex.Message} Fix it on the left, then Find on server again.";
            }
            else
            {
                _failures++;
                var wait = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, _failures - 1)));
                _retryAt = DateTime.UtcNow + wait;
                StatusText = $"Reading the log failed ({ex.Message}); trying again in {wait.TotalSeconds:0} s.";
            }
        }
        finally
        {
            _polling = false;
        }
        // the type or folder changed during this poll: read the new one now, not at the next tick
        if (!ReferenceEquals(tail, _tail) && _tail is not null)
            await PollAsync();
    }

    void Show(LogTail tail, IReadOnlyList<LogLine> lines)
    {
        foreach (var line in lines)
            _lines.Add(new LogLineItem(line));
        // news only: not the past read when a log is opened, nor lines held back while
        // following was paused or the connection was down
        if (!tail.LastPollWasBacklog)
        {
            // (not by the lines' times: the server's clock or time zone may differ from this PC's)
            var recent = lines.Where(l => l.Kind == LogLineKind.Chat).ToList();
            if (recent.Count > 0)
                ChatReceived?.Invoke(this, recent);
        }
        while (_lines.Count > MaxLines)
            _lines.RemoveAt(0);
        StatusText = tail.CurrentFile is null
            ? $"No {tail.Type} log yet."
            : $"{tail.CurrentFile} · {(Follow ? "following" : "paused")} · {DateTime.Now:HH:mm:ss}";
    }

    // ---- replying ----

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        var text = Message.Trim();
        if (text.Length == 0)
            return;
        if (!_main.IsSessionActive)
        {
            StatusText = "Connect over RCON to broadcast a message.";
            return;
        }
        var reply = await _main.RunAsync(ServerMessage.BuildCommand(text));
        if (reply is not null && ServerMessage.Interpret(reply) != CommandOutcome.Failed)
        {
            Message = "";
            await PollAsync();
        }
        else
        {
            StatusText = "The message was not sent: see the console.";
        }
    }

    bool _closed;
    Exception? _openError;

    // failures in a row, and when to try again: a host that is down is not asked every 5 s
    int _failures;
    DateTime _retryAt;

    /// <summary>One poll may take this long before it is given up (a dead link must not freeze following).</summary>
    static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    public void Close()
    {
        _closed = true;
        _timer.Stop();
        _folder?.Dispose();
    }
}

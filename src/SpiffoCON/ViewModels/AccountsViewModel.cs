using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;

namespace SpiffoCON.ViewModels;

public sealed record AccountRow(AccountInfo Account, CharacterInfo? Character, bool Online, IReadOnlyList<UserLogEntry> History)
{
    public string Username => Account.Username;
    public string Role => Account.Role;
    public bool Banned => Account.Banned;
    public string? CharacterName => Character?.Name;
    public string Position => Character is { } c ? $"{c.X},{c.Y},{c.Z}" : "";
    public string Status => Banned ? "banned" : Online ? "online" : Character?.Dead == true ? "dead" : "";
    public string LastSeen => Account.LastConnection?.ToString("dd/MM/yy HH:mm") ?? "";
    // the server bumps "amount" on an existing row with the same reason instead of adding one
    public int Kicks => Count("Kicked");
    public int Bans => Count("Banned");

    int Count(string type) => History.Where(h => h.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).Sum(h => Math.Max(1, h.Amount));
}

/// <summary>
/// Every account the server knows, online or not, from a copy of its SQLite databases
/// (Zomboid/db/&lt;server&gt;.db and Saves/Multiplayer/&lt;server&gt;/players.db) read over SFTP.
/// Read only: changes go through RCON (ban, unban, access level).
/// </summary>
public sealed partial class AccountsViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly ObservableCollection<AccountRow> _rows = [];
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    IServerDbSource? _source;
    ServerDatabaseSnapshot? _snapshot;

    public AccountsViewModel(MainViewModel main)
    {
        _main = main;
        View = new ListCollectionView(_rows) { Filter = Matches };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            View.Refresh();
        };
        // the "online" column follows the Players list
        _main.OnlinePlayersChanged += (_, _) => Rebuild();
    }

    /// <summary>Asks for a local Zomboid folder; set by the view.</summary>
    public Func<string?>? PickFolder { get; set; }

    public ICollectionView View { get; }
    public ObservableCollection<string> Servers { get; } = [];
    public ObservableCollection<UserLogEntry> History { get; } = [];
    public ObservableCollection<BannedIp> BannedIps { get; } = [];
    public ObservableCollection<BannedSteamId> BannedSteamIds { get; } = [];

    [ObservableProperty] private string? selectedServer;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private bool onlyBanned;
    [ObservableProperty] private string source = "No database read yet.";
    [ObservableProperty] private string statusText = "Read the server's accounts over SFTP (Find on server), or open a local Zomboid folder.";
    [ObservableProperty] private bool isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private AccountRow? selected;

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanged(AccountRow? value)
    {
        History.Clear();
        foreach (var h in value?.History.OrderByDescending(h => h.When) ?? Enumerable.Empty<UserLogEntry>())
            History.Add(h);
    }

    partial void OnOnlyBannedChanged(bool value) => View.Refresh();

    partial void OnSearchChanged(string value)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    partial void OnSelectedServerChanged(string? value)
    {
        if (_source is not null && value is not null && _loadingServers == false)
            _ = ReadAsync();
    }

    bool _loadingServers;

    /// <summary>The account of a player, when the accounts were read (for the character window).</summary>
    internal AccountRow? Find(string username) =>
        _rows.FirstOrDefault(r => r.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

    bool Matches(object o)
    {
        var row = (AccountRow)o;
        if (OnlyBanned && !row.Banned)
            return false;
        var s = Search.Trim();
        return s.Length == 0
            || row.Username.Contains(s, StringComparison.CurrentCultureIgnoreCase)
            || row.CharacterName?.Contains(s, StringComparison.CurrentCultureIgnoreCase) == true
            || row.Account.SteamId?.Contains(s, StringComparison.OrdinalIgnoreCase) == true
            || row.Role.Contains(s, StringComparison.OrdinalIgnoreCase);
    }

    // ---- loading ----

    [RelayCommand]
    private async Task FindOnServerAsync()
    {
        var sftp = _main.CurrentSftpSettings();
        if (sftp is null)
        {
            StatusText = "Enable SFTP on the left (user and password) to read the server's databases.";
            return;
        }
        var zomboid = _main.Profile.SftpZomboidFolder;
        if (zomboid is null)
        {
            StatusText = "Looking for the Zomboid folder over SFTP...";
            try
            {
                var probe = await _main.ProbeAsync(sftp);
                // from now on the key this probe saw is required
                sftp = _main.CurrentSftpSettings() ?? sftp;
                var logs = probe.LogFolders.OrderBy(p => p.Contains("/Zomboid/", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Length).FirstOrDefault();
                if (logs is null)
                {
                    StatusText = "No Zomboid folder found over SFTP.";
                    return;
                }
                zomboid = logs[..logs.TrimEnd('/').LastIndexOf('/')];
                _main.Profile.SftpZomboidFolder = zomboid;
                _main.SaveProfile();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                StatusText = "SFTP: " + ex.Message;
                return;
            }
        }
        await OpenAsync(new SftpServerDbSource(sftp, zomboid));
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (PickFolder?.Invoke() is { } path)
            await OpenAsync(new LocalServerDbSource(path));
    }

    async Task OpenAsync(IServerDbSource source)
    {
        StatusText = "Looking for databases in " + source.Description + "...";
        try
        {
            var servers = await source.ListServersAsync();
            if (servers.Count == 0)
            {
                StatusText = "No db/<server>.db in that Zomboid folder.";
                return;
            }
            _source = source;
            Source = source.Description;
            _loadingServers = true;
            Servers.Clear();
            foreach (var s in servers)
                Servers.Add(s);
            // the sandbox file chosen earlier names the server: pzserver_SandboxVars.lua → pzserver
            var hint = Path.GetFileName(_main.Profile.SftpSandboxPath ?? "").Replace("_SandboxVars.lua", "");
            SelectedServer = Servers.FirstOrDefault(s => s == hint) ?? Servers[0];
            _loadingServers = false;
            await ReadAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _loadingServers = false;
            StatusText = "Could not read the databases: " + ex.Message;
        }
    }

    int _readCount;

    [RelayCommand]
    private async Task ReadAsync()
    {
        if (_source is null || SelectedServer is null)
            return;
        // a newer read (another server picked, another folder) wins over one still running
        int read = ++_readCount;
        var source = _source;
        var temp = Path.Combine(Path.GetTempPath(), "SpiffoCON", "db-" + Guid.NewGuid().ToString("N"));
        StatusText = $"Reading {SelectedServer}...";
        try
        {
            var (serverDb, playersDb) = await source.CopyAsync(SelectedServer, temp);
            var snapshot = await Task.Run(() => ServerDatabase.Read(serverDb, playersDb));
            if (read != _readCount)
                return;
            _snapshot = snapshot;
            IsLoaded = true;
            Rebuild();
            StatusText = $"{_snapshot.Accounts.Count} accounts, {_snapshot.Characters.Count} characters, " +
                         $"{_snapshot.Accounts.Count(a => a.Banned)} banned · read at {DateTime.Now:HH:mm:ss}" +
                         (playersDb is null ? " · no players.db yet (nobody has played)" : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (read == _readCount)
                StatusText = "Could not read the databases: " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    void Rebuild()
    {
        if (_snapshot is null)
            return;
        var keep = Selected?.Username;
        var online = _main.OnlinePlayers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var history = _snapshot.UserLog.ToLookup(l => l.Username, StringComparer.OrdinalIgnoreCase);
        var characters = _snapshot.Characters.Where(c => c.PlayerIndex == 0).GroupBy(c => c.Username, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _rows.Clear();
        foreach (var a in _snapshot.Accounts)
            _rows.Add(new AccountRow(a, characters.GetValueOrDefault(a.Username), online.Contains(a.Username), history[a.Username].ToList()));
        Selected = _rows.FirstOrDefault(r => r.Username == keep);

        BannedIps.Clear();
        foreach (var b in _snapshot.BannedIps)
            BannedIps.Add(b);
        BannedSteamIds.Clear();
        foreach (var b in _snapshot.BannedSteamIds)
            BannedSteamIds.Add(b);
    }

    // ---- actions (over RCON) ----

    [RelayCommand]
    private void UseInPlayersTab()
    {
        if (Selected is not { } row)
            return;
        _main.Players.Target = row.Username;
        if (row.Character is { } c)
        {
            _main.Players.CoordX = c.X.ToString();
            _main.Players.CoordY = c.Y.ToString();
            _main.Players.CoordZ = c.Z.ToString();
        }
        StatusText = $"{row.Username}" + (row.Character is null ? "" : " and their last saved position") + " are now in the Players tab.";
    }

    [RelayCommand]
    private async Task UnbanAsync()
    {
        if (Selected is not { } row || !_main.IsSessionActive)
        {
            StatusText = "Connect over RCON to unban.";
            return;
        }
        if (_main.Confirm?.Invoke($"Unban {row.Username}?") != true)
            return;
        var reply = await _main.RunAsync(PlayerCommands.Unban(row.Username));
        StatusText = reply?.Trim() ?? "Failed: see the console.";
        await ReadAsync();
    }

    [RelayCommand]
    private async Task BanAsync()
    {
        if (Selected is not { } row || !_main.IsSessionActive)
        {
            StatusText = "Connect over RCON to ban.";
            return;
        }
        if (_main.Confirm?.Invoke($"Ban {row.Username}?" + (row.Online ? " They are online and will be kicked." : "")) != true)
            return;
        var reply = await _main.RunAsync(PlayerCommands.Ban(row.Username, false, null));
        StatusText = reply?.Trim() ?? "Failed: see the console.";
        await ReadAsync();
    }
}

using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Commands;

namespace SpiffoCON.ViewModels;

/// <summary>
/// Online players and the actions on one player: moderation, access level, teleport, powers, XP.
/// The target is a free text field so offline players can be banned or unbanned too.
/// </summary>
public sealed partial class PlayersViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(30) };

    public PlayersViewModel(MainViewModel main)
    {
        _main = main;
        _main.OnlinePlayersChanged += (_, _) => UpdateCount();
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSessionActive))
            {
                UpdatePolling();
                UpdateCount();
            }
        };
        _poll.Tick += async (_, _) => await _main.RefreshPlayersAsync(quiet: true);
        UpdateCount();
    }

    public ObservableCollection<string> OnlinePlayers => _main.OnlinePlayers;

    public IReadOnlyList<string> AccessLevels => PlayerCommands.AccessLevels;

    public IReadOnlyList<Perk> Perks => PlayerCommands.Perks;

    [ObservableProperty] private string onlineCount = "";
    [ObservableProperty] private bool autoRefresh = true;

    partial void OnAutoRefreshChanged(bool value) => UpdatePolling();

    void UpdatePolling()
    {
        if (AutoRefresh && _main.IsSessionActive)
            _poll.Start();
        else
            _poll.Stop();
    }

    void UpdateCount() => OnlineCount = _main.IsSessionActive ? $"{OnlinePlayers.Count} online" : "Not connected";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _main.RefreshPlayersAsync();
        UpdateCount();
    }

    // ---- target ----

    [ObservableProperty] private string? selectedOnline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTarget))]
    private string target = "";

    public bool HasTarget => Target.Trim().Length > 0;

    partial void OnSelectedOnlineChanged(string? value)
    {
        if (value is not null)
            Target = value;
    }

    [ObservableProperty] private string result = "";

    // ---- moderation ----

    [ObservableProperty] private string reason = "";
    [ObservableProperty] private bool banIp;

    [RelayCommand]
    private Task KickAsync() => RunAsync(PlayerCommands.Kick(Target, Reason), $"Kick {Target.Trim()}?", refresh: true);

    [RelayCommand]
    private Task BanAsync() => RunAsync(PlayerCommands.Ban(Target, BanIp, Reason),
        $"Ban {Target.Trim()}{(BanIp ? " and their IP address" : "")}? Online players are kicked too.", refresh: true);

    [RelayCommand]
    private Task UnbanAsync() => RunAsync(PlayerCommands.Unban(Target));

    [RelayCommand]
    private Task VoiceBanAsync(string on) => RunAsync(PlayerCommands.VoiceBan(Target, on == "true"));

    // ---- access level ----

    [ObservableProperty] private string accessLevel = "none";

    [RelayCommand]
    private Task SetAccessLevelAsync() => RunAsync(PlayerCommands.SetAccessLevel(Target, AccessLevel),
        AccessLevel is "Admin" or "Moderator" ? $"Give {Target.Trim()} the {AccessLevel} access level?" : null);

    // ---- teleport ----

    [ObservableProperty] private string? teleportTarget;
    [ObservableProperty] private string coordX = "";
    [ObservableProperty] private string coordY = "";
    [ObservableProperty] private string coordZ = "0";

    [RelayCommand]
    private Task TeleportToPlayerAsync()
    {
        if (string.IsNullOrWhiteSpace(TeleportTarget))
        {
            Result = "Choose the player to teleport to.";
            return Task.CompletedTask;
        }
        if (TeleportTarget.Trim().Equals(Target.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Result = "A player can't be teleported to themselves.";
            return Task.CompletedTask;
        }
        return RunAsync(PlayerCommands.TeleportToPlayer(Target, TeleportTarget));
    }

    [RelayCommand]
    private Task TeleportToCoordinatesAsync()
    {
        if (!int.TryParse(CoordX.Trim(), out var x) || !int.TryParse(CoordY.Trim(), out var y) || !int.TryParse(CoordZ.Trim(), out var z))
        {
            Result = "Coordinates must be whole numbers (x, y and floor z).";
            return Task.CompletedTask;
        }
        return RunAsync(PlayerCommands.TeleportToCoordinates(Target, x, y, z));
    }

    // ---- powers ----

    [RelayCommand]
    private Task GodModeAsync(string on) => RunAsync(PlayerCommands.GodMode(Target, on == "true"));

    [RelayCommand]
    private Task InvisibleAsync(string on) => RunAsync(PlayerCommands.Invisible(Target, on == "true"));

    [RelayCommand]
    private Task NoClipAsync(string on) => RunAsync(PlayerCommands.NoClip(Target, on == "true"));

    // ---- XP ----

    [ObservableProperty] private Perk? selectedPerk = PlayerCommands.Perks[0];
    [ObservableProperty] private string xpAmount = "100";
    [ObservableProperty] private bool useMultiplier;

    [RelayCommand]
    private Task AddXpAsync()
    {
        if (SelectedPerk is null || !int.TryParse(XpAmount.Trim(), out var amount) || amount <= 0)
        {
            Result = "Choose a skill and a positive amount of XP.";
            return Task.CompletedTask;
        }
        return RunAsync(PlayerCommands.AddXp(Target, SelectedPerk.Id, amount, UseMultiplier));
    }

    // ----

    async Task RunAsync(string command, string? confirm = null, bool refresh = false)
    {
        if (!_main.IsSessionActive)
        {
            Result = "Not connected.";
            return;
        }
        if (!HasTarget)
        {
            Result = "Choose a player.";
            return;
        }
        if (confirm is not null && _main.Confirm?.Invoke(confirm) != true)
            return;

        var reply = await _main.RunAsync(command);
        Result = reply is null
            ? "Failed: see the console."
            : PlayerCommands.Interpret(reply) switch
            {
                CommandOutcome.Success => reply.Trim(),
                CommandOutcome.Failed => "Failed: " + reply.Trim(),
                _ => "Sent; the server gave no reply.",
            };
        if (refresh)
            await _main.RefreshPlayersAsync();
    }
}

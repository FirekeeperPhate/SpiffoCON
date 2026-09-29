using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Commands;

namespace SpiffoCON.ViewModels;

public enum EventTarget { Random, Everyone, Chosen }

public sealed record EventLogLine(DateTime Time, string Text, bool Failed)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>Weather (rain, storms) and events placed on players (lightning, thunder, helicopter...).</summary>
public sealed partial class EventsViewModel : ObservableObject
{
    const int MaxLogLines = 500;

    readonly MainViewModel _main;
    readonly DispatcherTimer _rainStop = new();
    DateTime _rainStopAt;

    public EventsViewModel(MainViewModel main)
    {
        _main = main;
        _rainStop.Tick += async (_, _) => await StopRainOnTimerAsync();
    }

    public ObservableCollection<EventLogLine> Log { get; } = [];

    // ---- weather ----

    [ObservableProperty] private int rainIntensity = 50;
    [ObservableProperty] private bool rainStopsAfter;
    [ObservableProperty] private string rainMinutes = "10";
    [ObservableProperty] private string stormHours = "2";
    [ObservableProperty] private string rainTimerText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RainTimerVisible))]
    private bool rainTimerRunning;

    public bool RainTimerVisible => RainTimerRunning;

    [RelayCommand]
    private async Task StartRainAsync()
    {
        int? minutes = null;
        if (RainStopsAfter)
        {
            if (!int.TryParse(RainMinutes.Trim(), out var m) || m is < 1 or > 1440)
            {
                Add("The rain duration must be 1 to 1440 minutes.", failed: true);
                return;
            }
            minutes = m;
        }
        if (!await RunAsync(EventCommands.StartRain(RainIntensity), $"Rain, intensity {RainIntensity}"))
            return;
        CancelRainTimer();
        if (minutes is { } after)
        {
            _rainStopAt = DateTime.Now.AddMinutes(after);
            _rainStop.Interval = TimeSpan.FromMinutes(after);
            _rainStop.Start();
            RainTimerRunning = true;
            RainTimerText = $"The rain stops at {_rainStopAt:HH:mm} (keep SpiffoCON open).";
        }
    }

    [RelayCommand]
    private async Task StartStormAsync()
    {
        if (!int.TryParse(StormHours.Trim(), out var hours) || hours is < 1 or > 168)
        {
            Add("The storm duration must be 1 to 168 game hours.", failed: true);
            return;
        }
        if (await RunAsync(EventCommands.StartStorm(hours), $"Storm, {hours} game hour{(hours == 1 ? "" : "s")}"))
            CancelRainTimer();
    }

    [RelayCommand]
    private async Task StopRainAsync()
    {
        if (await RunAsync(EventCommands.StopRain, "Stop rain"))
            CancelRainTimer();
    }

    [RelayCommand]
    private async Task StopWeatherAsync()
    {
        if (await RunAsync(EventCommands.StopWeather, "Stop all weather"))
            CancelRainTimer();
    }

    [RelayCommand]
    private void CancelRainTimer()
    {
        _rainStop.Stop();
        RainTimerRunning = false;
        RainTimerText = "";
    }

    async Task StopRainOnTimerAsync()
    {
        CancelRainTimer();
        if (!_main.IsSessionActive)
        {
            Add("The rain was due to stop, but SpiffoCON is not connected: stop it by hand.", failed: true);
            return;
        }
        await RunAsync(EventCommands.StopRain, "Stop rain (timer)");
    }

    // ---- events on players ----

    public IReadOnlyList<WorldEvent> Events => EventCommands.Events;

    public ObservableCollection<string> OnlinePlayers => _main.OnlinePlayers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerTargeted), nameof(ServerPicksTarget), nameof(IsHorde), nameof(IsChopper))]
    private WorldEvent selectedEvent = EventCommands.Events[0];

    public bool IsChopper => SelectedEvent.Id == "chopper";

    [RelayCommand]
    private Task StopChopperAsync() => RunAsync(EventCommands.StopChopper, "Stop the helicopter");

    /// <summary>The event goes to players SpiffoCON chooses (not to one the server picks).</summary>
    public bool PlayerTargeted => SelectedEvent.Targeting == EventTargeting.Player;

    public bool ServerPicksTarget => !PlayerTargeted;

    public bool IsHorde => SelectedEvent.Id == "horde";

    [ObservableProperty] private EventTarget target = EventTarget.Random;
    [ObservableProperty] private string? chosenPlayer;
    [ObservableProperty] private string hordeSize = "20";

    public bool TargetRandom
    {
        get => Target == EventTarget.Random;
        set { if (value) Target = EventTarget.Random; }
    }

    public bool TargetEveryone
    {
        get => Target == EventTarget.Everyone;
        set { if (value) Target = EventTarget.Everyone; }
    }

    public bool TargetChosen
    {
        get => Target == EventTarget.Chosen;
        set { if (value) Target = EventTarget.Chosen; }
    }

    partial void OnTargetChanged(EventTarget value)
    {
        OnPropertyChanged(nameof(TargetRandom));
        OnPropertyChanged(nameof(TargetEveryone));
        OnPropertyChanged(nameof(TargetChosen));
    }

    partial void OnChosenPlayerChanged(string? value)
    {
        if (value is not null)
            Target = EventTarget.Chosen;
    }

    [RelayCommand]
    private async Task TriggerAsync()
    {
        if (!_main.IsSessionActive)
        {
            Add("Not connected.", failed: true);
            return;
        }
        var e = SelectedEvent;
        await _main.RefreshPlayersAsync(quiet: true);
        var online = _main.OnlinePlayers.ToList();
        if (online.Count == 0)
        {
            Add($"{e.Name}: nobody is online.", failed: true);
            return;
        }

        int horde = 0;
        if (IsHorde && (!int.TryParse(HordeSize.Trim(), out horde) || horde < 1 || horde > EventCommands.MaxHorde))
        {
            Add($"The horde size must be 1 to {EventCommands.MaxHorde}.", failed: true);
            return;
        }

        List<string?> players;
        if (!PlayerTargeted)
            players = [null];
        else
        {
            switch (Target)
            {
                case EventTarget.Everyone:
                    players = [.. online];
                    break;
                case EventTarget.Chosen:
                    if (string.IsNullOrWhiteSpace(ChosenPlayer))
                    {
                        Add("Choose the player.", failed: true);
                        return;
                    }
                    if (!online.Contains(ChosenPlayer))
                    {
                        Add($"{ChosenPlayer} is not online.", failed: true);
                        return;
                    }
                    players = [ChosenPlayer];
                    break;
                default:
                    players = [online[Random.Shared.Next(online.Count)]];
                    break;
            }
        }

        if (IsHorde)
        {
            var who = players.Count == 1 ? players[0] : $"each of the {players.Count} online players";
            if (_main.Confirm?.Invoke($"Spawn {horde} zombies around {who}?") != true)
                return;
        }

        foreach (var player in players)
        {
            var label = player is null ? $"{e.Name} (the server picks the player)" : $"{e.Name} on {player}";
            if (IsHorde)
                label = $"Horde of {horde} on {player}";
            await RunAsync(EventCommands.Build(e, player, horde), label);
            if (_unanswered)
            {
                // the server did not answer: the other players are skipped, not tried one timeout each
                if (players.Count > 1)
                    Add("Stopped: the server did not answer; the other players were skipped.", failed: true);
                break;
            }
        }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    // ----

    /// <summary>Runs one command and logs the outcome; true when the server confirmed it.</summary>
    /// <summary>The last command got no reply at all (not sent, timed out, connection down).</summary>
    bool _unanswered;

    async Task<bool> RunAsync(string command, string label)
    {
        if (!_main.IsSessionActive)
        {
            Add("Not connected.", failed: true);
            return false;
        }
        var reply = await _main.RunAsync(command);
        _unanswered = reply is null;
        if (reply is null)
        {
            Add($"{label}: failed, see the console.", failed: true);
            return false;
        }
        var outcome = EventCommands.Interpret(reply);
        var text = reply.Trim().Length == 0 ? "no reply" : reply.Trim();
        Add($"{label}: {text}", failed: outcome == CommandOutcome.Failed);
        return outcome != CommandOutcome.Failed;
    }

    void Add(string text, bool failed = false)
    {
        Log.Insert(0, new EventLogLine(DateTime.Now, text, failed));
        while (Log.Count > MaxLogLines)
            Log.RemoveAt(Log.Count - 1);
    }

    public void Close() => _rainStop.Stop();
}

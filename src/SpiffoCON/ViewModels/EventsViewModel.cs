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
        Bridge.PropertyChanged += OnBridgeChanged;
        ShowWeather(null);
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
        // as chosen now: the event can be changed while the online list is read
        var e = SelectedEvent;
        bool isHorde = IsHorde, targeted = PlayerTargeted;
        await _main.RefreshPlayersAsync(quiet: true);
        var online = _main.OnlinePlayers.ToList();
        if (online.Count == 0)
        {
            Add($"{e.Name}: nobody is online.", failed: true);
            return;
        }

        int horde = 0;
        if (isHorde && (!int.TryParse(HordeSize.Trim(), out horde) || horde < 1 || horde > EventCommands.MaxHorde))
        {
            Add($"The horde size must be 1 to {EventCommands.MaxHorde}.", failed: true);
            return;
        }

        List<string?> players;
        if (!targeted)
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

        if (isHorde)
        {
            var who = players.Count == 1 ? players[0] : $"each of the {players.Count} online players";
            if (_main.Confirm?.Invoke($"Spawn {horde} zombies around {who}?") != true)
                return;
        }

        foreach (var player in players)
        {
            var label = player is null ? $"{e.Name} (the server picks the player)" : $"{e.Name} on {player}";
            if (isHorde)
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

    // ---- weather (bridge v5): the overrides of the game's own admin Climate panel ----

    public BridgeViewModel Bridge => _main.Bridge;

    [ObservableProperty] private bool fogOn;
    [ObservableProperty] private double fog = 50;
    [ObservableProperty] private bool cloudsOn;
    [ObservableProperty] private double clouds = 50;
    [ObservableProperty] private bool windOn;
    [ObservableProperty] private double wind = 20;
    [ObservableProperty] private bool temperatureOn;
    [ObservableProperty] private double temperature = 20;
    [ObservableProperty] private bool snowOn;
    [ObservableProperty] private double snow = 50;
    [ObservableProperty] private string weatherStatus = "";
    [ObservableProperty] private string weatherResult = "";
    [ObservableProperty] private bool isSettingWeather;

    /// <summary>The controls show what the server has, once per bridge connection (not over the user's edits).</summary>
    bool _weatherLoaded;

    void OnBridgeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BridgeViewModel.IsConnected) && !Bridge.IsConnected)
            _weatherLoaded = false;
        if (e.PropertyName is nameof(BridgeViewModel.World) or nameof(BridgeViewModel.IsConnected) or nameof(BridgeViewModel.BridgeVersion))
            ShowWeather(Bridge.World);
    }

    void ShowWeather(Core.Bridge.BridgeWorld? w)
    {
        if (!Bridge.IsConnected || w is null)
        {
            WeatherStatus = "Fog, clouds, wind, temperature and snow are set through the SpiffoCON Bridge (v5): connect it in the Bridge tab.";
            return;
        }
        if (!_weatherLoaded)
        {
            _weatherLoaded = true;
            (FogOn, Fog) = (w.AdminFog is not null, (w.AdminFog ?? w.Fog ?? 0.5) * 100);
            (CloudsOn, Clouds) = (w.AdminClouds is not null, (w.AdminClouds ?? w.Clouds ?? 0.5) * 100);
            (WindOn, Wind) = (w.AdminWind is not null, w.AdminWind ?? w.Wind ?? 20);
            (TemperatureOn, Temperature) = (w.AdminTemperature is not null, w.AdminTemperature ?? w.Temperature ?? 20);
            (SnowOn, Snow) = (w.AdminSnow is not null, (w.AdminSnow ?? 0.5) * 100);
        }
        var set = new List<string>();
        if (w.AdminFog is { } f) set.Add($"fog {f:P0}");
        if (w.AdminClouds is { } c) set.Add($"clouds {c:P0}");
        if (w.AdminWind is { } wi) set.Add($"wind {wi:0} km/h");
        if (w.AdminTemperature is { } t) set.Add($"{t:0.#} °C");
        if (w.AdminSnow is { } s) set.Add($"snowfall {s:P0}");
        var now = new List<string>();
        if (w.Fog is { } nf) now.Add($"fog {nf:P0}");
        if (w.Clouds is { } nc) now.Add($"clouds {nc:P0}");
        if (w.Wind is { } nw) now.Add($"wind {nw:0} km/h");
        if (w.Temperature is { } nt) now.Add($"{nt:0.#} °C");
        if (w.Rain is > 0) now.Add((w.Snow == true ? "snow " : "rain ") + $"{w.Rain:P0}");
        WeatherStatus = (set.Count > 0 ? "Set by SpiffoCON: " + string.Join(", ", set) + "." : "The game decides the weather.")
            + (now.Count > 0 ? " Now: " + string.Join(", ", now) + "." : "")
            + (Bridge.BridgeVersion is > 0 and < 5 ? $" Setting it needs bridge v5 (the server runs v{Bridge.BridgeVersion})." : "");
    }

    [RelayCommand]
    private async Task ApplyWeatherAsync()
    {
        var settings = new List<(Core.Bridge.BridgeClient.ClimateSetting, double?)>
        {
            (Core.Bridge.BridgeClient.ClimateSetting.Fog, FogOn ? Math.Round(Fog) / 100 : null),
            (Core.Bridge.BridgeClient.ClimateSetting.Clouds, CloudsOn ? Math.Round(Clouds) / 100 : null),
            (Core.Bridge.BridgeClient.ClimateSetting.Wind, WindOn ? Math.Round(Wind) : null),
            (Core.Bridge.BridgeClient.ClimateSetting.Temperature, TemperatureOn ? Math.Round(Temperature) : null),
            (Core.Bridge.BridgeClient.ClimateSetting.Snow, SnowOn ? Math.Round(Snow) / 100 : null),
        };
        await SetWeatherAsync(settings, reset: false);
    }

    [RelayCommand]
    private async Task ResetWeatherAsync()
    {
        if (await SetWeatherAsync([], reset: true))
            (FogOn, CloudsOn, WindOn, TemperatureOn, SnowOn) = (false, false, false, false, false);
    }

    async Task<bool> SetWeatherAsync(IReadOnlyList<(Core.Bridge.BridgeClient.ClimateSetting, double?)> settings, bool reset)
    {
        if (IsSettingWeather)
            return false;
        IsSettingWeather = true;
        WeatherResult = "Sending to the bridge...";
        try
        {
            var problem = await Bridge.SetWeatherAsync(settings, reset);
            WeatherResult = problem ?? (reset
                ? $"Weather given back to the game at {DateTime.Now:HH:mm:ss}."
                : $"Set at {DateTime.Now:HH:mm:ss}. Players see it at the next climate update (within ten game minutes).");
            return problem is null;
        }
        finally
        {
            IsSettingWeather = false;
        }
    }

    // ---- time of day (bridge v7) ----

    [ObservableProperty] private string timeOfDay = "08:00";

    /// <summary>"8", "8:30", "20.15": the hour of the day with decimals, or null.</summary>
    internal static double? ParseTime(string text)
    {
        var parts = text.Trim().Split(':', '.', ',');
        if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out int hour) || hour is < 0 or > 23)
            return null;
        int minute = 0;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out minute) || minute is < 0 or > 59))
            return null;
        return hour + minute / 60.0;
    }

    [RelayCommand]
    private async Task SetTimeAsync()
    {
        if (ParseTime(TimeOfDay) is not { } hour)
        {
            WeatherResult = "Time of day: hours and minutes, like 8:30 or 21:00.";
            return;
        }
        string time = $"{(int)hour:00}:{Math.Round((hour - (int)hour) * 60):00}";
        if (Bridge.V7Problem is { } notNow)
        {
            WeatherResult = notNow;
            return;
        }
        if (IsSettingWeather)
            return;
        // the game's clock only goes forward, to the next time it is that hour: how far that is depends on
        // the time it is now, read now (the last refresh may be minutes old, and a few game minutes past the
        // hour asked for means a whole day)
        string skip = "";
        if (await Bridge.ReadWorldAsync() is { Hour: { } nowHour, Minute: { } nowMinute })
        {
            double hours = hour - (nowHour + nowMinute / 60.0);
            bool nextDay = hours <= 0;
            if (nextDay)
                hours += 24;
            // the bridge refuses it too: a time just behind the clock is a slip, not a wish for tomorrow
            if (hours > 23.5)
            {
                WeatherResult = $"It is {nowHour:00}:{nowMinute:00} in game, and the clock can only skip forward: {time} would be almost a whole day away. "
                    + "Choose a time at least half an hour from now.";
                return;
            }
            skip = $" It is {nowHour:00}:{nowMinute:00} in game: about {(hours < 1 ? $"{Math.Round(hours * 60):0} minutes" : $"{hours:0.#} hours")} pass"
                + (nextDay ? ", into the next day." : ".");
        }
        if (_main.Confirm?.Invoke($"Skip forward to {time} for everyone?\n\n"
                + "The game's clock can't go back, so it jumps to the next time it is that hour." + skip
                + " The world gets that much older, and with it what the game ages by its clock (food, for one). Players see it within ten seconds.") != true)
            return;
        IsSettingWeather = true;
        WeatherResult = "Sending to the bridge...";
        try
        {
            WeatherResult = await Bridge.SetTimeAsync(hour)
                ?? $"Time of day set to {time} at {DateTime.Now:HH:mm:ss}" + (Bridge.World?.SkippedHours is { } skipped ? $": {skipped:0.#} game hours skipped." : ".");
        }
        finally
        {
            IsSettingWeather = false;
        }
    }

    public void Close()
    {
        _rainStop.Stop();
        Bridge.PropertyChanged -= OnBridgeChanged;
    }
}

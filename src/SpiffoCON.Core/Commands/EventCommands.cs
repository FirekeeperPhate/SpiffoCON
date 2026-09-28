using System.Globalization;

namespace SpiffoCON.Core.Commands;

/// <summary>What a world event can target.</summary>
public enum EventTargeting
{
    /// <summary>The command names a player (lightning, thunder, createhorde).</summary>
    Player,

    /// <summary>The server itself picks a random online player (gunshot, chopper).</summary>
    ServerPicks,
}

/// <summary>An event placed on players.</summary>
public sealed record WorldEvent(string Id, string Name, string Description, EventTargeting Targeting);

/// <summary>
/// Weather and event commands. Syntax and replies come from the B42 server classes
/// (zombie.commands.serverCommands: StartRainCommand, StartStormCommand, LightningCommand...).
/// </summary>
public static class EventCommands
{
    public const int MinRainIntensity = 1;
    public const int MaxRainIntensity = 100;

    /// <summary>The server caps hordes too; this keeps one click from flooding a server.</summary>
    public const int MaxHorde = 500;

    public static IReadOnlyList<WorldEvent> Events { get; } =
    [
        new("lightning", "Lightning", "A lightning strike (flash and thunderclap) at the player's position.", EventTargeting.Player),
        new("thunder", "Thunder", "A thunderclap at the player's position.", EventTargeting.Player),
        new("chopper", "Helicopter", "The helicopter event: a chopper flies over and draws zombies. The server picks a random online player.", EventTargeting.ServerPicks),
        new("gunshot", "Gunshot", "A loud gunshot that draws zombies. The server picks a random online player.", EventTargeting.ServerPicks),
        new("horde", "Zombie horde", "Spawns zombies around the player.", EventTargeting.Player),
    ];

    /// <summary>startrain: intensity 1-100 (the server divides it by 100).</summary>
    public static string StartRain(int intensity) =>
        string.Create(CultureInfo.InvariantCulture, $"startrain {Math.Clamp(intensity, MinRainIntensity, MaxRainIntensity)}");

    /// <summary>startstorm: duration in game hours.</summary>
    public static string StartStorm(double gameHours) =>
        string.Create(CultureInfo.InvariantCulture, $"startstorm {Math.Max(1, (int)Math.Round(gameHours))}");

    public const string StopRain = "stoprain";

    /// <summary>Calls off the helicopter event ("Chopper deactivated").</summary>
    public const string StopChopper = "chopper stop";

    /// <summary>Stops rain and any storm or other weather.</summary>
    public const string StopWeather = "stopweather";

    /// <summary>The command for an event on a player (ignored by events the server targets itself).</summary>
    public static string Build(WorldEvent e, string? player, int hordeCount = 20) => e.Id switch
    {
        "lightning" => $"lightning {Q(player)}",
        "thunder" => $"thunder {Q(player)}",
        "horde" => string.Create(CultureInfo.InvariantCulture, $"createhorde {Math.Clamp(hordeCount, 1, MaxHorde)} {Q(player)}"),
        "chopper" => "chopper",
        "gunshot" => "gunshot",
        _ => throw new ArgumentException("Unknown event " + e.Id, nameof(e)),
    };

    static readonly string[] SuccessPhrases =
    [
        "Rain started", "Thunderstorm started", "Rain stopped", "Weather stopped",
        "Lightning triggered", "Thunder triggered", "Chopper launched", "Chopper activated", "Chopper deactivated", "Gunshot fired", "Horde spawned",
    ];

    static readonly string[] ErrorPhrases =
    [
        "not found", "Pass a username", "Specify a player", "Invalid", "No such user", "Unknown", "Not enough rights",
        "Wrong arguments", "Error",
    ];

    /// <summary>Replies like "Rain started", "Lightning triggered", "User \"x\" not found".</summary>
    public static CommandOutcome Interpret(string reply)
    {
        // the fixed success texts first: an error phrase may be part of a player's name
        if (SuccessPhrases.Any(p => reply.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return CommandOutcome.Success;
        if (ErrorPhrases.Any(p => reply.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return CommandOutcome.Failed;
        return CommandOutcome.Unconfirmed;
    }

    static string Q(string? player) => string.IsNullOrWhiteSpace(player)
        ? throw new ArgumentException("This event needs a player.", nameof(player))
        : CommandText.Quote(player.Trim());
}

using System.Globalization;

namespace SpiffoCON.Core.Commands;

public sealed record Perk(string Id, string Name);

/// <summary>
/// Commands that act on players. Syntax follows the @CommandArgs annotations of the B42 server
/// (zombie.commands.serverCommands), e.g. kickuser (.+) [-r (.+)], teleportto (.+) x,y,z.
/// </summary>
public static class PlayerCommands
{
    /// <summary>
    /// setaccesslevel values, as a B42 server lists them when given an unknown one ("banned" is left
    /// out: banning has its own command). They are case-sensitive: "Moderator" is rejected. The
    /// in-game help text still names the B41 levels (Overseer, none), which B42 no longer accepts.
    /// </summary>
    public static IReadOnlyList<string> AccessLevels { get; } = ["user", "priority", "observer", "gm", "moderator", "admin"];

    /// <summary>Skill ids accepted by addxp (PerkFactory.Perks) with their English names.</summary>
    public static IReadOnlyList<Perk> Perks { get; } =
    [
        new("Fitness", "Fitness"), new("Strength", "Strength"),
        new("Sprinting", "Running"), new("Lightfoot", "Lightfooted"), new("Nimble", "Nimble"), new("Sneak", "Sneaking"),
        new("Axe", "Axe"), new("Blunt", "Long Blunt"), new("SmallBlunt", "Short Blunt"), new("LongBlade", "Long Blade"),
        new("SmallBlade", "Short Blade"), new("Spear", "Spear"), new("Maintenance", "Maintenance"),
        new("Aiming", "Aiming"), new("Reloading", "Reloading"),
        new("Woodwork", "Carpentry"), new("Cooking", "Cooking"), new("Farming", "Agriculture"), new("Doctor", "First Aid"),
        new("Electricity", "Electrical"), new("MetalWelding", "Welding"), new("Mechanics", "Mechanics"), new("Tailoring", "Tailoring"),
        new("Blacksmith", "Blacksmithing"), new("Melting", "Smelting"), new("Carving", "Carving"), new("FlintKnapping", "Knapping"),
        new("Masonry", "Masonry"), new("Pottery", "Pottery"), new("Glassmaking", "Glassmaking"),
        new("Fishing", "Fishing"), new("Trapping", "Trapping"), new("PlantScavenging", "Foraging"), new("Tracking", "Tracking"),
        new("Husbandry", "Animal Care"), new("Butchering", "Butchering"),
    ];

    /// <summary>
    /// Parses the "players" reply: "Players connected (2):" then one "-name" per line
    /// (the server uses " &lt;LINE&gt; " instead of line breaks when the command comes from chat).
    /// </summary>
    public static IReadOnlyList<string> ParsePlayers(string reply) =>
        reply.Replace("<LINE>", "\n").ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('-') && l.Length > 1)
            .Select(l => l[1..].Trim())
            .ToList();

    public static string AddItem(string player, string fullType, int count) =>
        $"additem {Q(player)} {Q(fullType)} {Math.Max(1, count)}";

    public static string AddVehicle(string fullType, string player) =>
        $"addvehicle {Q(fullType)} {Q(player)}";

    public static string Kick(string player, string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? $"kickuser {Q(player)}" : $"kickuser {Q(player)} -r {Q(reason.Trim())}";

    public static string Ban(string player, bool banIp, string? reason)
    {
        var command = $"banuser {Q(player)}";
        if (banIp)
            command += " -ip";
        if (!string.IsNullOrWhiteSpace(reason))
            command += $" -r {Q(reason.Trim())}";
        return command;
    }

    public static string Unban(string player) => $"unbanuser {Q(player)}";

    public static string SetAccessLevel(string player, string level) => $"setaccesslevel {Q(player)} {Q(level.ToLowerInvariant())}";

    /// <summary>Moves <paramref name="player"/> to <paramref name="target"/>.</summary>
    public static string TeleportToPlayer(string player, string target) => $"teleportplayer {Q(player)} {Q(target)}";

    public static string TeleportToCoordinates(string player, int x, int y, int z) =>
        string.Create(CultureInfo.InvariantCulture, $"teleportto {Q(player)} {x},{y},{z}");

    public static string GodMode(string player, bool on) => $"godmodeplayer {Q(player)} {Flag(on)}";

    public static string Invisible(string player, bool on) => $"invisibleplayer {Q(player)} {Flag(on)}";

    public static string NoClip(string player, bool on) => $"noclip {Q(player)} {Flag(on)}";

    public static string VoiceBan(string player, bool on) => $"voiceban {Q(player)} {Flag(on)}";

    /// <summary>"-true" makes the server apply the XP multiplier.</summary>
    public static string AddXp(string player, string perkId, int amount, bool useMultiplier) =>
        $"addxp {Q(player)} {perkId}={amount}" + (useMultiplier ? " -true" : "");

    /// <summary>"Item Base.Axe Added in Rick's inventory." / "No such user".</summary>
    public static CommandOutcome InterpretAddItem(string reply, string? player = null)
    {
        if (reply.Contains("Added in", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Success;
        return IsError(reply, player) ? CommandOutcome.Failed : CommandOutcome.Unconfirmed;
    }

    /// <summary>"Vehicle spawned" / "Unknown vehicle script ..." / "User ... not found".</summary>
    public static CommandOutcome InterpretAddVehicle(string reply, string? player = null)
    {
        if (reply.Contains("Vehicle spawned", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Success;
        return IsError(reply, player) ? CommandOutcome.Failed : CommandOutcome.Unconfirmed;
    }

    /// <summary>
    /// For the other player commands: their success texts vary ("User rj kicked.", "User rj is now
    /// invincible."), their failures share a few phrases taken from the server code.
    /// </summary>
    public static CommandOutcome Interpret(string reply, string? player = null)
    {
        if (IsError(reply, player))
            return CommandOutcome.Failed;
        return reply.Trim().Length > 0 ? CommandOutcome.Success : CommandOutcome.Unconfirmed;
    }

    static readonly string[] ErrorPhrases =
    [
        "No such user", "not found", "doesn't exist", "Can't find", "can't be", "No connection for player",
        "Wrong arguments", "Not enough rights", "Unknown", "Invalid", "Error", "not match", "Observer can only",
    ];

    /// <summary>
    /// An error phrase counts unless it lies entirely inside the echoed player name: a player
    /// called "InvalidSam" must not turn "User InvalidSam kicked." into a failure, and a player
    /// called "User" must not hide the "user" of "No such user".
    /// </summary>
    static bool IsError(string reply, string? player)
    {
        var name = player?.Trim() ?? "";
        var names = new List<(int Start, int End)>();
        for (int i = name.Length == 0 ? -1 : reply.IndexOf(name, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = reply.IndexOf(name, i + 1, StringComparison.OrdinalIgnoreCase))
            names.Add((i, i + name.Length));

        foreach (var phrase in ErrorPhrases)
            for (int i = reply.IndexOf(phrase, StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = reply.IndexOf(phrase, i + 1, StringComparison.OrdinalIgnoreCase))
                if (!names.Any(n => i >= n.Start && i + phrase.Length <= n.End))
                    return true;
        return false;
    }

    static string Q(string value) => CommandText.Quote(value.Trim());

    static string Flag(bool on) => on ? "-true" : "-false";
}

namespace SpiffoCON.Core.Commands;

/// <summary>Commands that act on players, with their PZ reply texts.</summary>
public static class PlayerCommands
{
    /// <summary>
    /// Parses the "players" reply: "Players connected (2):" followed by "-name" lines.
    /// </summary>
    public static IReadOnlyList<string> ParsePlayers(string reply) =>
        reply.ReplaceLineEndings("\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('-') && l.Length > 1)
            .Select(l => l[1..].Trim())
            .ToList();

    public static string AddItem(string player, string fullType, int count) =>
        $"additem {CommandText.Quote(player)} {CommandText.Quote(fullType)} {Math.Max(1, count)}";

    public static string AddVehicle(string fullType, string player) =>
        $"addvehicle {CommandText.Quote(fullType)} {CommandText.Quote(player)}";

    /// <summary>"Item Base.Axe Added in Rick's inventory." / "No such user".</summary>
    public static CommandOutcome InterpretAddItem(string reply)
    {
        if (reply.Contains("Added in", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Success;
        return IsKnownError(reply) ? CommandOutcome.Failed : CommandOutcome.Unconfirmed;
    }

    /// <summary>"Vehicle spawned" / "Unknown vehicle script ..." / "User ... not found".</summary>
    public static CommandOutcome InterpretAddVehicle(string reply)
    {
        if (reply.Contains("Vehicle spawned", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Success;
        return IsKnownError(reply) ? CommandOutcome.Failed : CommandOutcome.Unconfirmed;
    }

    static bool IsKnownError(string reply) =>
        reply.Contains("No such user", StringComparison.OrdinalIgnoreCase)
        || reply.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || reply.Contains("Unknown", StringComparison.OrdinalIgnoreCase)
        || reply.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase)
        || reply.Contains("Invalid", StringComparison.OrdinalIgnoreCase);
}

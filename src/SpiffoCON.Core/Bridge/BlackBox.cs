using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpiffoCON.Core.Bridge;

public sealed record BlackBoxPlayer
{
    public string? Name { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Z { get; init; }

    /// <summary>In a vehicle.</summary>
    public bool? Car { get; init; }
}

/// <summary>A line of the black box: what a minute of the server was like, or (<see cref="Start"/>) a start of the server.</summary>
public sealed record BlackBoxLine
{
    /// <summary>Unix milliseconds, as text (a number that size is not safe in Lua's output).</summary>
    public string? Time { get; init; }
    public bool? Start { get; init; }
    public List<BlackBoxPlayer>? Players { get; init; }
    public int? Zombies { get; init; }
    public int? Vehicles { get; init; }

    /// <summary>The slowest update of that minute, in milliseconds.</summary>
    public double? Worst { get; init; }

    /// <summary>Megabytes, when the game tells them.</summary>
    public double? MemoryUsed { get; init; }
    public double? MemoryMax { get; init; }

    [JsonIgnore]
    public DateTimeOffset? When => long.TryParse(Time, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms)
        ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}

/// <summary>
/// The bridge's black box (bridge v21): spiffocon_blackbox.txt, one JSON line a minute and one at each start of the
/// server, written whatever else happens, so that what a server was doing before it crashed can be read after.
/// </summary>
public static class BlackBox
{
    public const string File = "spiffocon_blackbox.txt";

    /// <summary>The lines of the file; one that can't be read (the server died writing it) is left out.</summary>
    public static List<BlackBoxLine> Parse(string text)
    {
        var lines = new List<BlackBoxLine>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            try
            {
                if (JsonSerializer.Deserialize(line, BridgeJson.Default.BlackBoxLine) is { } read)
                    lines.Add(read);
            }
            catch (JsonException)
            {
            }
        return lines;
    }

    /// <summary>The minutes of the run before the one going on: those between the last two starts.</summary>
    public static List<BlackBoxLine> RunBefore(IReadOnlyList<BlackBoxLine> lines)
    {
        int now = -1;
        for (int i = lines.Count - 1; i >= 0 && now < 0; i--)
            if (lines[i].Start == true)
                now = i;
        var run = new List<BlackBoxLine>();
        for (int i = now - 1; i >= 0 && lines[i].Start != true; i--)
            run.Insert(0, lines[i]);
        return run;
    }

    /// <summary>What to show of a run: its last minutes, one line each (times of this PC), then who was where last.</summary>
    public static List<string> Describe(IReadOnlyList<BlackBoxLine> run, int minutes = 10)
    {
        var text = new List<string>();
        foreach (var line in run.TakeLast(minutes))
        {
            var parts = new List<string> { Plural(line.Players?.Count ?? 0, "player") };
            if (line.Zombies is { } zombies)
                parts.Add(Plural(zombies, "zombie") + " loaded");
            if (line.Vehicles is { } vehicles)
                parts.Add(Plural(vehicles, "vehicle"));
            if (line.MemoryUsed is { } used)
                parts.Add($"memory {used:0}" + (line.MemoryMax is { } max ? $" of {max:0} MB" : " MB"));
            if (line.Worst is { } worst)
                parts.Add($"slowest update {worst:0} ms");
            text.Add($"{line.When?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? "?"}  {string.Join(", ", parts)}");
        }
        if (run.Count > 0 && run[^1].Players is { Count: > 0 } players)
            text.Add("Last seen: " + string.Join("; ", players.Select(p => $"{p.Name} at {p.X},{p.Y},{p.Z}" + (p.Car == true ? " in a vehicle" : ""))));
        return text;
    }

    static string Plural(int count, string what) => count.ToString(CultureInfo.InvariantCulture) + " " + what + (count == 1 ? "" : "s");
}

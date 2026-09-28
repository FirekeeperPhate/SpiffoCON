using SpiffoCON.Core.Catalog;

namespace SpiffoCON.Core.Commands;

/// <summary>
/// Server options as printed by the RCON "showoptions" command ("* Mods=...") or as written in
/// the server .ini ("Mods=..."). Both are "key=value" lines.
/// </summary>
public sealed class ServerOptions
{
    readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public static ServerOptions Parse(string text)
    {
        var options = new ServerOptions();
        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim().TrimStart('*').Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            options._values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return options;
    }

    public string? this[string key] => _values.GetValueOrDefault(key);

    public int Count => _values.Count;

    public IEnumerable<string> Names => _values.Keys;

    /// <summary>Mod ids in load order, without B42's leading backslash.</summary>
    public IReadOnlyList<string> Mods => SplitList(this["Mods"]).Select(ModInfo.NormalizeId).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> WorkshopItems => SplitList(this["WorkshopItems"]).Where(id => id.All(char.IsAsciiDigit)).Distinct().ToList();

    static IEnumerable<string> SplitList(string? value) =>
        (value ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SpiffoCON.Core.Commands;

/// <summary>Kind of a server option, as the B42 server reports it (ConfigOption.getType()).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ServerOptionType>))]
public enum ServerOptionType
{
    [JsonStringEnumMemberName("boolean")] Boolean,
    [JsonStringEnumMemberName("integer")] Integer,
    [JsonStringEnumMemberName("double")] Double,
    /// <summary>1-based index into <see cref="ServerOptionInfo.Values"/>.</summary>
    [JsonStringEnumMemberName("enum")] Enum,
    [JsonStringEnumMemberName("string")] String,
    /// <summary>Long text; line breaks are written as &lt;LINE&gt;.</summary>
    [JsonStringEnumMemberName("text")] Text,
}

public sealed record ServerOptionInfo
{
    public required string Name { get; init; }
    public required ServerOptionType Type { get; init; }
    public string Default { get; init; } = "";
    public string? Min { get; init; }
    public string? Max { get; init; }
    public IReadOnlyList<string> Values { get; init; } = [];
    public string? Description { get; init; }

    /// <summary>Display name (sandbox options have one; server options show their key).</summary>
    public string? Title { get; init; }

    /// <summary>The option has no meaningful default (per-server random ids); never reset it.</summary>
    public bool NoDefault { get; init; }

    /// <summary>Shown next to the option, and asked about before changing it.</summary>
    public string? Warning { get; init; }

    /// <summary>For options the server reports but the bundled metadata doesn't know.</summary>
    public static ServerOptionInfo Unknown(string name) => new() { Name = name, Type = ServerOptionType.String };
}

public sealed record ServerOptionPage(string Name, string Title, IReadOnlyList<ServerOptionInfo> Options);

public sealed record ValidationResult(bool Ok, string Value, string? Error)
{
    public static ValidationResult Valid(string value) => new(true, value, null);
    public static ValidationResult Invalid(string error) => new(false, "", error);
}

public enum ChangeOutcome { Applied, Rejected, UnknownOption, Unconfirmed }

public sealed record ChangeResult(ChangeOutcome Outcome, string? ServerValue);

/// <summary>
/// Metadata of the B42 server options (dumped from a real server, see tools/MakeServerOptions.cs)
/// and the rules for changing them over RCON. What a real server does with bad input:
/// an invalid value is ignored and the old one kept (the reply shows which), a decimal comma is
/// ignored, and an out-of-range enum index makes the command throw with no reply at all.
/// </summary>
public static partial class ServerOptionCatalog
{
    static readonly Lazy<IReadOnlyList<ServerOptionPage>> LoadedPages = new(() => Load("server-options.json"));
    static readonly Lazy<IReadOnlyList<ServerOptionPage>> LoadedSandboxPages = new(() => Load("sandbox-options.json"));

    /// <summary>Server options (the .ini), changed over RCON.</summary>
    public static IReadOnlyList<ServerOptionPage> Pages => LoadedPages.Value;

    /// <summary>Sandbox options (SandboxVars.lua), changed in the file; the server reads it at start-up.</summary>
    public static IReadOnlyList<ServerOptionPage> SandboxPages => LoadedSandboxPages.Value;

    public static ServerOptionInfo? Find(string name) => Find(Pages, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Sandbox paths are case-sensitive Lua keys ("ZombieLore.Speed").</summary>
    public static ServerOptionInfo? FindSandbox(string path) => Find(SandboxPages, path, StringComparison.Ordinal);

    static ServerOptionInfo? Find(IReadOnlyList<ServerOptionPage> pages, string name, StringComparison comparison) =>
        pages.SelectMany(p => p.Options).FirstOrDefault(o => o.Name.Equals(name, comparison));

    static IReadOnlyList<ServerOptionPage> Load(string file)
    {
        using var stream = typeof(ServerOptionCatalog).Assembly.GetManifestResourceStream("SpiffoCON.Core.Data." + file)
            ?? throw new InvalidOperationException($"{file} is missing from the build.");
        return JsonSerializer.Deserialize(stream, OptionsJson.Default.ListServerOptionPage) ?? [];
    }

    /// <summary>Checks and normalizes a value before it is sent (invariant numbers, "true"/"false").</summary>
    public static ValidationResult Validate(ServerOptionInfo option, string input)
    {
        var value = input.Trim();
        switch (option.Type)
        {
            case ServerOptionType.Boolean:
                return value.ToLowerInvariant() is "true" or "false"
                    ? ValidationResult.Valid(value.ToLowerInvariant())
                    : ValidationResult.Invalid("Must be true or false.");

            case ServerOptionType.Integer:
            case ServerOptionType.Enum:
                if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return ValidationResult.Invalid("Must be a whole number.");
                if (option.Type == ServerOptionType.Enum && option.Values.Count > 0 && (i < 1 || i > option.Values.Count))
                    return ValidationResult.Invalid($"Choose one of the {option.Values.Count} values.");
                return InRange(option, i) ?? ValidationResult.Valid(i.ToString(CultureInfo.InvariantCulture));

            case ServerOptionType.Double:
                // accept "12,5" from an Italian keyboard but always send "12.5": the server ignores commas
                var normalized = value.Replace(',', '.');
                if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
                    return ValidationResult.Invalid("Must be a number.");
                return InRange(option, d) ?? ValidationResult.Valid(d.ToString("0.##########", CultureInfo.InvariantCulture));

            case ServerOptionType.Text when UsesBackslashN(option):
                return ValidationResult.Valid(string.Join("\\n", input.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim())));

            case ServerOptionType.Text:
                // the .ini is line based: line breaks become the chat's <LINE> tag, written the way
                // the game writes it ("a <LINE> <LINE> b", single spaces, also for empty lines)
                var parts = new List<string>();
                var lines = input.ReplaceLineEndings("\n").Split('\n');
                for (int n = 0; n < lines.Length; n++)
                {
                    if (n > 0)
                        parts.Add("<LINE>");
                    if (lines[n].Trim().Length > 0)
                        parts.Add(lines[n].Trim());
                }
                return ValidationResult.Valid(string.Join(' ', parts));

            default:
                if (input.Contains('\n') || input.Contains('\r'))
                    return ValidationResult.Invalid("Must be a single line.");
                return ValidationResult.Valid(input.Trim());
        }
    }

    static ValidationResult? InRange(ServerOptionInfo option, double value)
    {
        if (option.Min is not null && double.TryParse(option.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && value < min)
            return ValidationResult.Invalid($"Minimum is {option.Min}.");
        if (option.Max is not null && double.TryParse(option.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && value > max)
            return ValidationResult.Invalid($"Maximum is {option.Max}.");
        return null;
    }

    /// <summary>changeoption Name "value" (the server saves it to the .ini at once).</summary>
    public static string ChangeCommand(string name, string value) => $"changeoption {name} {CommandText.Quote(value)}";

    /// <summary>
    /// "Option : PVP is now : false" (enums add the label: "2(kick)") / "Option X doesn't exist.".
    /// Applied only if the value the server now has is the one we sent.
    /// </summary>
    public static ChangeResult InterpretChange(string reply, string sentValue)
    {
        // the success form first: a text value may itself contain "doesn't exist"
        var m = ChangeReply().Match(reply);
        if (!m.Success)
            return new ChangeResult(
                reply.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ? ChangeOutcome.UnknownOption : ChangeOutcome.Unconfirmed, null);
        var now = m.Groups[1].Value.TrimEnd('\r', '\n');
        // the command quoting turns " into ', so compare the same way
        var expected = sentValue.Replace('"', '\'');
        if (SameValue(now, expected))
            return new ChangeResult(ChangeOutcome.Applied, now);
        // enums answer "2(kick)": only a number followed by a label is one (a text may end in ")")
        if (EnumLabel().Match(now) is { Success: true } label)
            now = label.Groups[1].Value;
        return new ChangeResult(SameValue(now, expected) ? ChangeOutcome.Applied : ChangeOutcome.Rejected, now);
    }

    static bool SameValue(string a, string b)
    {
        if (a == b)
            return true;
        return double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && Math.Abs(x - y) < 1e-9;
    }

    /// <summary>Editor text for a stored value: &lt;LINE&gt; back to line breaks for text options.</summary>
    public static string ToEditorText(ServerOptionInfo option, string value) => option.Type switch
    {
        ServerOptionType.Text when UsesBackslashN(option) => value.Replace("\\n", "\n"),
        ServerOptionType.Text => LineTag().Replace(value, "\n"),
        _ => value,
    };

    /// <summary>
    /// The server browser description breaks lines with a literal "\n" (its own help text says so);
    /// the welcome message, shown in chat, uses &lt;LINE&gt;.
    /// </summary>
    static bool UsesBackslashN(ServerOptionInfo option) => option.Name.Equals("PublicDescription", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"is now : ?(.*)$", RegexOptions.Singleline)]
    private static partial Regex ChangeReply();

    [GeneratedRegex(@"^(-?\d+)\(.*\)$")]
    private static partial Regex EnumLabel();

    [GeneratedRegex(@" ?<LINE> ?")]
    private static partial Regex LineTag();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<ServerOptionPage>))]
internal sealed partial class OptionsJson : JsonSerializerContext;

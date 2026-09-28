using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpiffoCON.Core.Commands;

public enum CommandOutcome
{
    /// <summary>The server confirmed the command.</summary>
    Success,

    /// <summary>The reply is empty or not recognized: the command may or may not have worked.</summary>
    Unconfirmed,

    /// <summary>The reply is a known error.</summary>
    Failed,
}

public sealed record MessageSegment(string Text, RgbColor Color);

/// <summary>
/// The broadcast message ("servermsg"). The editor text uses real line breaks and inline
/// &lt;RGB:r,g,b&gt; tags; line breaks become PZ's &lt;LINE&gt; tag when sent.
/// </summary>
public static partial class ServerMessage
{
    public const string LineTag = "<LINE>";

    /// <summary>Converts editor text to chat markup: one line per non-empty line, joined by &lt;LINE&gt;.</summary>
    public static string ToMarkup(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        // spaces around the tag keep it a separate token for PZ's rich-text parser
        return string.Join(" " + LineTag + " ", lines);
    }

    public static string BuildCommand(string text) => "servermsg " + CommandText.Quote(ToMarkup(text));

    public static CommandOutcome Interpret(string reply)
    {
        var r = reply.Trim();
        if (r.Contains("Message sent", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Success;
        if (r.Length == 0)
            return CommandOutcome.Unconfirmed;
        if (r.Contains("Unknown command", StringComparison.OrdinalIgnoreCase)
            || r.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Failed;
        return CommandOutcome.Unconfirmed;
    }

    /// <summary>
    /// Splits editor text into preview lines of colored segments. A color tag applies until the next
    /// one, across lines, which is how PZ's chat renders it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<MessageSegment>> Preview(string text, RgbColor defaultColor)
    {
        var result = new List<IReadOnlyList<MessageSegment>>();
        var color = defaultColor;
        var markup = ToMarkup(text).Replace('"', '\'');
        foreach (var line in markup.Split(LineTag))
        {
            var segments = new List<MessageSegment>();
            int pos = 0;
            foreach (Match m in RgbTag().Matches(line))
            {
                Add(segments, line[pos..m.Index], color);
                color = new RgbColor(Parse(m.Groups[1].Value), Parse(m.Groups[2].Value), Parse(m.Groups[3].Value));
                pos = m.Index + m.Length;
            }
            Add(segments, line[pos..], color);
            if (segments.Count > 0)
            {
                segments[0] = segments[0] with { Text = segments[0].Text.TrimStart() };
                segments[^1] = segments[^1] with { Text = segments[^1].Text.TrimEnd() };
                segments.RemoveAll(s => s.Text.Length == 0);
            }
            result.Add(segments);
        }
        return result;

        static void Add(List<MessageSegment> segments, string text, RgbColor color)
        {
            text = CollapseSpaces(text);
            if (text.Length > 0)
                segments.Add(new MessageSegment(text, color));
        }
    }

    static string CollapseSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (var c in text)
        {
            if (c == ' ')
            {
                if (!space)
                    sb.Append(c);
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString();
    }

    static float Parse(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? Math.Clamp(f, 0f, 1f) : 1f;

    [GeneratedRegex(@"<RGB:\s*([0-9.]+)\s*,\s*([0-9.]+)\s*,\s*([0-9.]+)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex RgbTag();
}

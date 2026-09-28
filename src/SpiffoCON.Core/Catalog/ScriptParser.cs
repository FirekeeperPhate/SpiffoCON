using System.Text;

namespace SpiffoCON.Core.Catalog;

/// <summary>A block of a PZ script file: "<c>item Axe { Weight = 3, ... }</c>".</summary>
public sealed class ScriptBlock(string type, string name)
{
    /// <summary>Everything in the header before the name ("item", "template vehicle", "module").</summary>
    public string Type { get; } = type;

    public string Name { get; } = name;

    /// <summary>"key = value" entries in file order; keys may repeat (e.g. "template").</summary>
    public List<KeyValuePair<string, string>> Values { get; } = [];

    public List<ScriptBlock> Children { get; } = [];

    public string? Get(string key)
    {
        for (int i = Values.Count - 1; i >= 0; i--)
            if (Values[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return Values[i].Value;
        return null;
    }
}

/// <summary>
/// Tolerant parser for PZ's script format (media/scripts/**/*.txt). Entries end at ',' or at a
/// line break; a header is whatever text precedes '{'. Handles /* */ and line-leading // comments.
/// Malformed input never throws: unbalanced braces just end the current block.
/// </summary>
public static class ScriptParser
{
    public static ScriptBlock Parse(string text)
    {
        var root = new ScriptBlock("", "");
        int pos = 0;
        ParseBody(StripComments(text), ref pos, root);
        return root;
    }

    static void ParseBody(string text, ref int pos, ScriptBlock block)
    {
        var buffer = new StringBuilder();
        while (pos < text.Length)
        {
            char c = text[pos++];
            switch (c)
            {
                case '{':
                    var (type, name) = SplitHeader(buffer.ToString());
                    buffer.Clear();
                    var child = new ScriptBlock(type, name);
                    ParseBody(text, ref pos, child);
                    block.Children.Add(child);
                    break;
                case '}':
                    Flush(buffer, block);
                    return;
                case ',':
                    Flush(buffer, block);
                    break;
                case '\n':
                    // a line without a trailing comma still ends a "key = value" entry,
                    // but a header ("item Axe") continues until its '{' on the next line
                    if (buffer.ToString().Contains('='))
                        Flush(buffer, block);
                    else
                        buffer.Append(' ');
                    break;
                case '\r':
                case '\t':
                    buffer.Append(' ');
                    break;
                default:
                    buffer.Append(c);
                    break;
            }
        }
        Flush(buffer, block);
    }

    static void Flush(StringBuilder buffer, ScriptBlock block)
    {
        var entry = buffer.ToString().Trim();
        buffer.Clear();
        if (entry.Length == 0)
            return;
        int eq = entry.IndexOf('=');
        if (eq < 0)
            block.Values.Add(new(entry, ""));
        else
            block.Values.Add(new(entry[..eq].Trim(), entry[(eq + 1)..].Trim()));
    }

    static (string Type, string Name) SplitHeader(string header)
    {
        var words = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length switch
        {
            0 => ("", ""),
            1 => (words[0], ""),
            _ => (string.Join(' ', words[..^1]), words[^1]),
        };
    }

    static string StripComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        bool lineStart = true;
        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (lineStart && text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                int end = text.IndexOf('\n', i);
                i = end < 0 ? text.Length : end;
                continue;
            }
            char c = text[i++];
            sb.Append(c);
            if (c == '\n')
                lineStart = true;
            else if (!char.IsWhiteSpace(c))
                lineStart = false;
        }
        return sb.ToString();
    }
}

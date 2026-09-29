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
    /// <summary>Deeper blocks are read as text: a mod file of 20 000 '{' must not overflow the stack.</summary>
    const int MaxDepth = 256;

    public static ScriptBlock Parse(string text)
    {
        var root = new ScriptBlock("", "");
        int pos = 0;
        // one kind of line end (a file may use CR alone), and no stray BOM from joined files
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('﻿', ' ');
        ParseBody(StripComments(text), ref pos, root, 0);
        return root;
    }

    static void ParseBody(string text, ref int pos, ScriptBlock block, int depth)
    {
        var buffer = new StringBuilder();
        // kept as a flag: searching the buffer at every line end is quadratic on long lines
        bool hasEquals = false;
        while (pos < text.Length)
        {
            char c = text[pos++];
            switch (c)
            {
                case '{' when depth < MaxDepth:
                    var (type, name) = SplitHeader(buffer.ToString());
                    buffer.Clear();
                    hasEquals = false;
                    var child = new ScriptBlock(type, name);
                    ParseBody(text, ref pos, child, depth + 1);
                    block.Children.Add(child);
                    break;
                case '}':
                    Flush(buffer, block);
                    return;
                case ',':
                    Flush(buffer, block);
                    hasEquals = false;
                    break;
                case '\n':
                    // a line without a trailing comma still ends a "key = value" entry,
                    // but a header ("item Axe") continues until its '{' on the next line
                    if (hasEquals)
                    {
                        Flush(buffer, block);
                        hasEquals = false;
                    }
                    else
                    {
                        buffer.Append(' ');
                    }
                    break;
                case '\t':
                    buffer.Append(' ');
                    break;
                default:
                    hasEquals |= c == '=';
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

    /// <summary>
    /// The game's zombie.scripting.ScriptParser.stripComments, ported step by step: from the last
    /// "*/" backwards, pairing nested comments ("/* a /* b */ c */"), so mod files read the same as
    /// in the game, oddities like "/*/" included.
    /// </summary>
    static string StripBlockComments(string text)
    {
        var sb = new StringBuilder(text);
        int end = JavaLastIndexOf(sb, "*/", sb.Length);
        while (end != -1)
        {
            int start = JavaLastIndexOf(sb, "/*", end - 1);
            if (start == -1)
                break;
            int innerEnd = JavaLastIndexOf(sb, "*/", end - 1);
            while (innerEnd > start)
            {
                int previous = start;
                start = JavaLastIndexOf(sb, "/*", start - 2);
                if (start == -1)
                    break;
                innerEnd = JavaLastIndexOf(sb, "*/", previous - 2);
            }
            if (start == -1)
                break;
            sb.Remove(start, end + 2 - start);
            end = JavaLastIndexOf(sb, "*/", start);
        }
        return sb.ToString();
    }

    /// <summary>Java's lastIndexOf(value, from): the last match starting at or before <paramref name="from"/>.</summary>
    static int JavaLastIndexOf(StringBuilder sb, string value, int from)
    {
        for (int i = Math.Min(from, sb.Length - value.Length); i >= 0; i--)
        {
            int k = 0;
            while (k < value.Length && sb[i + k] == value[k])
                k++;
            if (k == value.Length)
                return i;
        }
        return -1;
    }

    static string StripComments(string text)
    {
        text = StripBlockComments(text);
        var sb = new StringBuilder(text.Length);
        int i = 0;
        bool lineStart = true;
        while (i < text.Length)
        {
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

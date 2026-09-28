using System.Globalization;
using System.Text;

namespace SpiffoCON.Core.Files;

public enum LuaValueKind { Number, Boolean, String }

/// <summary>One "Key = value" of the file; <see cref="Path"/> joins nested tables with dots ("ZombieLore.Speed").</summary>
public sealed record SandboxValue(string Path, string Value, LuaValueKind Kind, int Start, int Length);

/// <summary>
/// The server's &lt;name&gt;_SandboxVars.lua: "SandboxVars = { VERSION = 6, Zombies = 4, ZombieLore = { Speed = 2, ... }, ... }".
/// Edits replace only the value literals in the original text, so comments, order, line endings
/// and settings of mods SpiffoCON doesn't know survive untouched.
/// </summary>
public sealed class SandboxVarsFile
{
    readonly Dictionary<string, SandboxValue> _values = new(StringComparer.Ordinal);

    SandboxVarsFile(string text) => Text = text;

    public string Text { get; }

    public IReadOnlyDictionary<string, SandboxValue> Values => _values;

    /// <summary>Throws <see cref="FormatException"/> (with the line) when the text isn't a SandboxVars table.</summary>
    public static SandboxVarsFile Parse(string text)
    {
        var file = new SandboxVarsFile(text);
        var lexer = new Lexer(text);
        lexer.Expect(TokenType.Name, "SandboxVars");
        lexer.Expect(TokenType.Equals);
        lexer.Expect(TokenType.OpenBrace);
        file.ParseTable(lexer, "");
        if (lexer.Next().Type != TokenType.End)
            throw lexer.Error("unexpected text after the SandboxVars table");
        return file;
    }

    void ParseTable(Lexer lexer, string prefix)
    {
        while (true)
        {
            var token = lexer.Next();
            switch (token.Type)
            {
                case TokenType.CloseBrace:
                    return;
                case TokenType.Comma:
                    continue;
                case TokenType.Name:
                    lexer.Expect(TokenType.Equals);
                    var path = prefix + token.Text;
                    var value = lexer.Next();
                    if (value.Type == TokenType.OpenBrace)
                        ParseTable(lexer, path + ".");
                    else if (value.Type is TokenType.Number or TokenType.Boolean or TokenType.String)
                        _values[path] = new SandboxValue(path, value.Text, (LuaValueKind)(value.Type - TokenType.Number), value.Start, value.Length);
                    else
                        throw lexer.Error($"unexpected value for {path}");
                    break;
                case TokenType.Number or TokenType.Boolean or TokenType.String:
                    break; // list item: not a setting, left as is
                case TokenType.OpenBrace:
                    ParseTable(lexer, prefix + "?."); // nested list item
                    break;
                default:
                    throw lexer.Error("expected a setting name or '}'");
            }
        }
    }

    /// <summary>
    /// The file text with the given values (path → new value) written in. Values are formatted for
    /// the literal they replace. Paths the file doesn't have are returned in <paramref name="missing"/>.
    /// </summary>
    public string With(IReadOnlyDictionary<string, string> changes, out IReadOnlyList<string> missing)
    {
        var edits = new List<(SandboxValue Target, string Literal)>();
        var notFound = new List<string>();
        foreach (var (path, value) in changes)
        {
            if (_values.TryGetValue(path, out var target))
                edits.Add((target, Format(target.Kind, value)));
            else
                notFound.Add(path);
        }
        missing = notFound;

        var sb = new StringBuilder(Text.Length + 64);
        int pos = 0;
        foreach (var (target, literal) in edits.OrderBy(e => e.Target.Start))
        {
            sb.Append(Text, pos, target.Start - pos).Append(literal);
            pos = target.Start + target.Length;
        }
        return sb.Append(Text, pos, Text.Length - pos).ToString();
    }

    static string Format(LuaValueKind kind, string value) => kind switch
    {
        LuaValueKind.Boolean => value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false",
        LuaValueKind.Number => FormatNumber(value),
        _ => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"",
    };

    /// <summary>Whole numbers stay whole; others use '.' whatever the PC's culture.</summary>
    static string FormatNumber(string value)
    {
        var v = value.Trim().Replace(',', '.');
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i.ToString(CultureInfo.InvariantCulture);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
            return d.ToString("0.0###########", CultureInfo.InvariantCulture);
        throw new FormatException($"'{value}' is not a number.");
    }

    // ---- lexer ----

    enum TokenType { Name, Equals, OpenBrace, CloseBrace, Comma, Number, Boolean, String, End }

    readonly record struct Token(TokenType Type, string Text, int Start, int Length);

    sealed class Lexer(string text)
    {
        int _pos;

        public Token Next()
        {
            SkipTrivia();
            if (_pos >= text.Length)
                return new Token(TokenType.End, "", _pos, 0);
            int start = _pos;
            char c = text[_pos];
            switch (c)
            {
                case '=': _pos++; return new Token(TokenType.Equals, "=", start, 1);
                case '{': _pos++; return new Token(TokenType.OpenBrace, "{", start, 1);
                case '}': _pos++; return new Token(TokenType.CloseBrace, "}", start, 1);
                case ',' or ';': _pos++; return new Token(TokenType.Comma, ",", start, 1);
                case '"' or '\'': return ReadString(c);
            }
            if (char.IsDigit(c) || c is '-' or '.' && _pos + 1 < text.Length && (char.IsDigit(text[_pos + 1]) || text[_pos + 1] == '.'))
            {
                _pos++;
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '.' || text[_pos] is '+' or '-' && text[_pos - 1] is 'e' or 'E'))
                    _pos++;
                return new Token(TokenType.Number, text[start.._pos], start, _pos - start);
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] == '_'))
                    _pos++;
                var word = text[start.._pos];
                return word is "true" or "false"
                    ? new Token(TokenType.Boolean, word, start, _pos - start)
                    : new Token(TokenType.Name, word, start, _pos - start);
            }
            throw Error($"unexpected character '{c}'");
        }

        Token ReadString(char quote)
        {
            int start = _pos++;
            var sb = new StringBuilder();
            while (_pos < text.Length && text[_pos] != quote)
            {
                char c = text[_pos++];
                if (c == '\n')
                    throw Error("line break inside a string");
                if (c == '\\' && _pos < text.Length)
                {
                    char e = text[_pos++];
                    sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => e });
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (_pos >= text.Length)
                throw Error("unterminated string");
            _pos++;
            return new Token(TokenType.String, sb.ToString(), start, _pos - start);
        }

        void SkipTrivia()
        {
            while (_pos < text.Length)
            {
                if (char.IsWhiteSpace(text[_pos]) || text[_pos] == '﻿')
                {
                    _pos++;
                }
                else if (text[_pos] == '-' && _pos + 1 < text.Length && text[_pos + 1] == '-')
                {
                    if (_pos + 3 < text.Length && text[_pos + 2] == '[' && text[_pos + 3] == '[')
                    {
                        int end = text.IndexOf("]]", _pos + 4, StringComparison.Ordinal);
                        _pos = end < 0 ? text.Length : end + 2;
                    }
                    else
                    {
                        int end = text.IndexOf('\n', _pos);
                        _pos = end < 0 ? text.Length : end + 1;
                    }
                }
                else
                {
                    break;
                }
            }
        }

        public void Expect(TokenType type, string? text = null)
        {
            var token = Next();
            if (token.Type != type || text is not null && token.Text != text)
                throw Error($"expected {text ?? type.ToString()}");
        }

        public FormatException Error(string message)
        {
            int line = 1;
            for (int i = 0; i < Math.Min(_pos, text.Length); i++)
                if (text[i] == '\n')
                    line++;
            return new FormatException($"SandboxVars.lua line {line}: {message}.");
        }
    }
}

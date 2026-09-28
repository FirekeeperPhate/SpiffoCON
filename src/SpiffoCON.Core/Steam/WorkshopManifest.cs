using System.Text;

namespace SpiffoCON.Core.Steam;

public sealed record InstalledWorkshopItem(string Id, long TimeUpdated, string Manifest);

/// <summary>
/// steamapps/workshop/appworkshop_108600.acf: what Steam or SteamCMD installed, with each item's
/// update time and manifest id. It describes the copy on that machine, even for unlisted items.
/// </summary>
public static class WorkshopManifest
{
    public const string FileName = "appworkshop_108600.acf";

    /// <summary>The manifest's path for a ".../steamapps/workshop/content/108600" folder (null for any other).</summary>
    public static string? PathFor(string workshopContentFolder)
    {
        var folder = workshopContentFolder.Replace('\\', '/').TrimEnd('/');
        const string suffix = "/content/108600";
        return folder.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? folder[..^suffix.Length] + "/" + FileName
            : null;
    }

    /// <summary>
    /// The installed items of the manifest next to a remote workshop content folder; null when
    /// there is none or it can't be read.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, InstalledWorkshopItem>?> ReadAsync(
        SpiffoCON.Core.Files.IRemoteFileSystem fs, string workshopContentFolder, CancellationToken ct = default)
    {
        if (PathFor(workshopContentFolder) is not { } path)
            return null;
        try
        {
            using var buffer = new MemoryStream();
            await fs.DownloadAsync(path, buffer, ct).ConfigureAwait(false);
            var items = Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            return items.Count > 0 ? items : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The WorkshopItemsInstalled section; empty when the text is not a workshop manifest.</summary>
    public static IReadOnlyDictionary<string, InstalledWorkshopItem> Parse(string text)
    {
        var result = new Dictionary<string, InstalledWorkshopItem>();
        if (Vdf.Parse(text.TrimStart('﻿')) is not { } root
            || root.GetValueOrDefault("AppWorkshop") is not Dictionary<string, object> app
            || Find(app, "WorkshopItemsInstalled") is not Dictionary<string, object> installed)
            return result;
        foreach (var (id, value) in installed)
        {
            if (value is not Dictionary<string, object> item)
                continue;
            long.TryParse(Find(item, "timeupdated") as string, out var updated);
            result[id] = new InstalledWorkshopItem(id, updated, Find(item, "manifest") as string ?? "");
        }
        return result;
    }

    static object? Find(Dictionary<string, object> node, string key) =>
        node.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>Valve's KeyValues text: "key" "value" pairs and "key" { ... } blocks.</summary>
    static class Vdf
    {
        public static Dictionary<string, object>? Parse(string text)
        {
            int i = 0;
            try
            {
                return Block(text, ref i, topLevel: true, depth: 0);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        static Dictionary<string, object> Block(string s, ref int i, bool topLevel, int depth)
        {
            // a manifest is a few levels deep; this only stops a broken file from overflowing the stack
            if (depth > 64)
                throw new FormatException("Too deeply nested.");
            var node = new Dictionary<string, object>();
            while (true)
            {
                var key = Token(s, ref i);
                if (key is null)
                    return topLevel ? node : throw new FormatException("Unclosed block.");
                if (key == "}")
                    return topLevel ? throw new FormatException("Unexpected }.") : node;
                var next = Token(s, ref i) ?? throw new FormatException("Missing value.");
                node[key] = next == "{" ? Block(s, ref i, topLevel: false, depth + 1) : next;
            }
        }

        /// <summary>A quoted string, "{" or "}"; null at the end. Skips // comments.</summary>
        static string? Token(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c))
                    i++;
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                    while (i < s.Length && s[i] != '\n')
                        i++;
                else
                    break;
            }
            if (i >= s.Length)
                return null;
            if (s[i] is '{' or '}')
                return s[i++].ToString();
            if (s[i] != '"')
                throw new FormatException("Expected a quoted string.");
            var sb = new StringBuilder();
            for (i++; i < s.Length && s[i] != '"'; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                    i++;
                sb.Append(s[i]);
            }
            if (i >= s.Length)
                throw new FormatException("Unclosed string.");
            i++;
            return sb.ToString();
        }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpiffoCON.Core.Catalog;

/// <summary>
/// Display names from media/lua/shared/Translate/&lt;LANG&gt;. B42 uses flat JSON
/// (ItemName.json: "Base.Axe": "Axe"; IG_UI.json: "IGUI_VehicleNameCarNormal": "..."); older mods
/// still ship the Lua-table .txt format (ItemName_EN.txt: ItemName_Base.Axe = "Axe",).
/// </summary>
public sealed partial class Translations
{
    const string VehiclePrefix = "IGUI_VehicleName";

    public Dictionary<string, string> ItemNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> VehicleNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Loads one Translate/&lt;LANG&gt; folder; later loads override earlier ones.</summary>
    public void LoadFolder(string languageFolder)
    {
        if (!Directory.Exists(languageFolder))
            return;
        foreach (var file in Directory.EnumerateFiles(languageFolder))
        {
            var name = Path.GetFileName(file);
            bool itemFile = name.StartsWith("ItemName", StringComparison.OrdinalIgnoreCase);
            bool uiFile = name.StartsWith("IG_UI", StringComparison.OrdinalIgnoreCase);
            if (!itemFile && !uiFile)
                continue;
            try
            {
                if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    LoadJson(File.ReadAllText(file), itemFile);
                else if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    LoadLegacy(ReadLegacyText(file), itemFile);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // a broken translation file only costs display names
            }
        }
    }

    void LoadJson(string json, bool itemFile)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return;
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                continue;
            Add(p.Name, p.Value.GetString() ?? "", itemFile);
        }
    }

    void LoadLegacy(string text, bool itemFile)
    {
        foreach (Match m in LegacyEntry().Matches(text))
            Add(m.Groups[1].Value, m.Groups[2].Value.Replace("\\\"", "\""), itemFile);
    }

    void Add(string key, string value, bool itemFile)
    {
        if (value.Length == 0)
            return;
        if (key.StartsWith(VehiclePrefix, StringComparison.OrdinalIgnoreCase))
            VehicleNames[key[VehiclePrefix.Length..]] = value;
        else if (itemFile)
            ItemNames[key.StartsWith("ItemName_", StringComparison.OrdinalIgnoreCase) ? key["ItemName_".Length..] : key] = value;
    }

    /// <summary>Legacy files are often not UTF-8 (B41 used per-language code pages).</summary>
    static string ReadLegacyText(string file)
    {
        var bytes = File.ReadAllBytes(file);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    [GeneratedRegex("([A-Za-z0-9_.\\-]+)\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex LegacyEntry();
}

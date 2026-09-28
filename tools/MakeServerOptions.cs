// Regenerates the option metadata bundled with SpiffoCON:
//   src/SpiffoCON.Core/Data/server-options.json   server options (the .ini)
//   src/SpiffoCON.Core/Data/sandbox-options.json  sandbox options (SandboxVars.lua)
//
// 1. Copy tools/DumpServerOptions.lua into <dedicated server>/media/lua/server/, start the
//    server once with -Duser.language=en (texts come out in the JVM's language), and take
//    spiffocon_options.json and spiffocon_sandbox.json from <cachedir>/Lua/. Remove the Lua file.
// 2. dotnet run tools/MakeServerOptions.cs -- <spiffocon_options.json> <spiffocon_sandbox.json> <dedicated server folder>
//
// Pages and their order come from the game's own settings screen
// (media/lua/client/OptionScreens/ServerSettingsScreen.lua, "INI" and "Sandbox" sections).
#:property PublishAot=false

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: MakeServerOptions <spiffocon_options.json> <spiffocon_sandbox.json> <dedicated server folder>");
    return 1;
}

var lua = File.ReadAllLines(Path.Combine(args[2], "media", "lua", "client", "OptionScreens", "ServerSettingsScreen.lua"));
var translate = Path.Combine(args[2], "media", "lua", "shared", "Translate", "EN");
var ui = JsonNode.Parse(File.ReadAllText(Path.Combine(translate, "UI.json")))!.AsObject();
var sandboxText = JsonNode.Parse(File.ReadAllText(Path.Combine(translate, "Sandbox.json")))!.AsObject();
var dataFolder = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "..", "src", "SpiffoCON.Core", "Data"));

// ---- server options ----
{
    var byName = Load(args[0]);
    var output = new JsonArray();
    var placed = new HashSet<string>();
    foreach (var (name, settings) in Pages("INI"))
        AddPage(output, name, ui[$"UI_ServerSettingGroup_{name}"]?.GetValue<string>() ?? name, settings, byName, placed);

    // options the game's screen doesn't show: grouped here so "Other" stays small
    var extra = new (string Page, string Pattern)[]
    {
        ("Anti-cheat", "^(AntiCheat.*|DoLuaChecksum|SteamVAC|MaxPacketsPerSecond)$"),
        ("Mods & map", "^(Mods|WorkshopItems|Map|Seed|SpawnPoint)$"),
        ("Chat", "^(BadWord.*|GoodWordListFile|ChatStreams)$"),
        ("PVP", "^(PVPLogTool.*|SafetyDisconnectDelay)$"),
        ("Voice", "^Voice.*$"),
        ("Vehicles", "^(SpeedLimit|CarEngineAttractionModifier|Disable\\w*Towing)$"),
    };
    foreach (var (pageName, pattern) in extra)
    {
        var names = byName.Keys.Where(k => !placed.Contains(k) && Regex.IsMatch(k, pattern)).Order().ToList();
        if (names.Count == 0)
            continue;
        var page = output.Select(p => p!.AsObject()).FirstOrDefault(p => (string)p["title"]! == pageName);
        if (page is null)
        {
            page = new JsonObject { ["name"] = pageName.Replace(" ", "").Replace("&", "And").Replace("-", ""), ["title"] = pageName, ["options"] = new JsonArray() };
            output.Add(page);
        }
        foreach (var n in names)
        {
            page["options"]!.AsArray().Add(Clean(byName[n]));
            placed.Add(n);
        }
    }
    int rest = AddOther(output, byName, placed);
    Save("server-options.json", output);
    Console.WriteLine($"server: {byName.Count} options in {output.Count} pages ({rest} under Other)");
}

// ---- sandbox options ----
{
    var byName = Load(args[1]);
    var output = new JsonArray();
    var placed = new HashSet<string>();
    foreach (var (name, settings) in Pages("Sandbox"))
        AddPage(output, name, sandboxText[$"Sandbox_{name}"]?.GetValue<string>() ?? SplitWords(name), settings, byName, placed);
    int rest = AddOther(output, byName, placed);
    Save("sandbox-options.json", output);
    Console.WriteLine($"sandbox: {byName.Count} options in {output.Count} pages ({rest} under Other)");
}
return 0;

// page name → setting names, for one top-level section of SettingsTable
List<(string Name, List<string> Settings)> Pages(string section)
{
    var pages = new List<(string Name, List<string> Settings)>();
    bool inside = false;
    foreach (var line in lua)
    {
        if (Regex.IsMatch(line, $"^\t\tname = \"{section}\""))
        {
            inside = true;
            continue;
        }
        if (inside && Regex.IsMatch(line, "^\t\tname = \""))
            break;
        if (!inside)
            continue;
        var page = Regex.Match(line, "^\t\t\t\tname = \"(\\w+)\"");
        if (page.Success)
            pages.Add((page.Groups[1].Value, []));
        // settings, not the preset entries of an advancedCombo (those names aren't options)
        foreach (Match setting in Regex.Matches(line, "\\{ *name = \"([\\w.]+)\""))
            if (pages.Count > 0)
                pages[^1].Settings.Add(setting.Groups[1].Value);
    }
    return pages;
}

static Dictionary<string, JsonObject> Load(string path) =>
    JsonNode.Parse(File.ReadAllText(path))!.AsArray().Select(n => n!.AsObject()).ToDictionary(o => (string)o["name"]!);

static void AddPage(JsonArray output, string name, string title, List<string> settings, Dictionary<string, JsonObject> byName, HashSet<string> placed)
{
    var options = new JsonArray();
    foreach (var s in settings.Where(s => byName.ContainsKey(s) && placed.Add(s)))
        options.Add(Clean(byName[s]));
    if (options.Count > 0)
        output.Add(new JsonObject { ["name"] = name, ["title"] = title, ["options"] = options });
}

// the game's "Other" page (if any) collects the leftovers, and goes last
static int AddOther(JsonArray output, Dictionary<string, JsonObject> byName, HashSet<string> placed)
{
    var other = output.Select(p => p!.AsObject()).FirstOrDefault(p => (string)p["name"]! == "Other");
    if (other is null)
        other = new JsonObject { ["name"] = "Other", ["title"] = "Other", ["options"] = new JsonArray() };
    else
        output.Remove(other);
    var rest = byName.Keys.Where(k => !placed.Contains(k)).Order().ToList();
    foreach (var k in rest)
        other["options"]!.AsArray().Add(Clean(byName[k]));
    if (other["options"]!.AsArray().Count > 0)
        output.Add(other);
    return rest.Count;
}

void Save(string file, JsonArray output) =>
    File.WriteAllText(Path.Combine(dataFolder, file), output.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }));

static string SplitWords(string name) => Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");

static JsonObject Clean(JsonObject source)
{
    var o = (JsonObject)source.DeepClone();
    o.Remove("value"); // the dumping server's current value, not metadata
    o.Remove("page");  // only set for mod options
    // random per server (two fresh servers differ only in these): the dump's "default" is just the
    // dumping server's value, so there is no default to reset to
    var warning = (string)o["name"]! switch
    {
        "ResetID" or "ServerPlayerID" => "Changing it makes every player create a new character (soft reset).",
        "Seed" => "Changing it changes how new parts of the map are generated; it is unique to this world.",
        _ => null,
    };
    if (warning is not null)
    {
        o["default"] = "";
        o["noDefault"] = true;
        o["warning"] = warning;
    }
    // untranslated enum labels ("Sandbox_StartDay_option3") become their number
    if (o["values"] is JsonArray values)
    {
        for (int i = 0; i < values.Count; i++)
            if (values[i]!.GetValue<string>() is var v && Regex.IsMatch(v, @"^\w+_option\d+$"))
                values[i] = (i + 1).ToString();
    }
    if (o["tooltip"] is { } tip)
    {
        // "\n" is a line break, except where the text talks about typing "\n" itself (" \n ")
        var text = Regex.Replace(tip.GetValue<string>(), @"(?<! )\\n|\\n(?! )", "\n");
        // the app shows min/max/default itself
        text = Regex.Replace(text, @"\s*Min: \S+ Max: \S+ Default: \S+\s*$", "");
        text = Regex.Replace(text, @"\s*Default\s*=.*$", "", RegexOptions.Multiline).Trim();
        o.Remove("tooltip");
        if (text.Length > 0)
            o["description"] = text;
    }
    return o;
}

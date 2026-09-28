// Regenerates src/SpiffoCON.Core/Data/server-options.json (server option metadata).
//
// 1. Copy tools/DumpServerOptions.lua into <dedicated server>/media/lua/server/, start the
//    server once with -Duser.language=en (tooltips come out in the JVM's language), and take
//    <cachedir>/Lua/spiffocon_options.json. Remove the Lua file afterwards.
// 2. dotnet run tools/MakeServerOptions.cs -- <spiffocon_options.json> <dedicated server folder>
//
// Pages and their order come from the game's own settings screen
// (media/lua/client/OptionScreens/ServerSettingsScreen.lua, "INI" section).
#:property PublishAot=false

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: MakeServerOptions <spiffocon_options.json> <dedicated server folder>");
    return 1;
}

var dump = JsonNode.Parse(File.ReadAllText(args[0]))!.AsArray();
var lua = File.ReadAllLines(Path.Combine(args[1], "media", "lua", "client", "OptionScreens", "ServerSettingsScreen.lua"));
var ui = JsonNode.Parse(File.ReadAllText(Path.Combine(args[1], "media", "lua", "shared", "Translate", "EN", "UI.json")))!.AsObject();

// pages of the INI section
var pages = new List<(string Name, List<string> Settings)>();
bool inIni = false;
foreach (var line in lua)
{
    if (Regex.IsMatch(line, "^\t\tname = \"INI\""))
        inIni = true;
    else if (Regex.IsMatch(line, "^\t\tname = \"") && inIni)
        break;
    if (!inIni)
        continue;
    var page = Regex.Match(line, "^\t\t\t\tname = \"(\\w+)\"");
    if (page.Success)
        pages.Add((page.Groups[1].Value, []));
    var setting = Regex.Match(line, "\\{ *name = \"(\\w+)\" *\\}");
    if (setting.Success && pages.Count > 0)
        pages[^1].Settings.Add(setting.Groups[1].Value);
}

var byName = dump.Select(n => n!.AsObject()).ToDictionary(o => (string)o["name"]!);
var placed = new HashSet<string>();
var output = new JsonArray();
foreach (var (name, settings) in pages)
{
    var options = new JsonArray();
    foreach (var s in settings.Where(byName.ContainsKey))
    {
        options.Add(Clean(byName[s]));
        placed.Add(s);
    }
    if (options.Count == 0)
        continue;
    var title = ui[$"UI_ServerSettingGroup_{name}"]?.GetValue<string>() ?? name;
    output.Add(new JsonObject { ["name"] = name, ["title"] = title, ["options"] = options });
}
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

// the game has its own "Other" page: the leftovers join it, and it goes last
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

var target = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "..", "src", "SpiffoCON.Core", "Data", "server-options.json"));
File.WriteAllText(target, output.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
Console.WriteLine($"{byName.Count} options in {output.Count} pages ({rest.Count} under Other) -> {target}");
return 0;

static JsonObject Clean(JsonObject source)
{
    var o = (JsonObject)source.DeepClone();
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
    if (o["tooltip"] is { } tip)
    {
        // the game appends "Min: 0 Max: 1000 Default: 2"; the app shows those itself
        // "\n" is a line break, except where the text talks about typing "\n" itself (" \n ")
        var text = Regex.Replace(tip.GetValue<string>(), @"(?<! )\\n|\\n(?! )", "\n");
        text = Regex.Replace(text, @"\s*Min: \S+ Max: \S+ Default: \S+\s*$", "").Trim();
        o.Remove("tooltip");
        if (text.Length > 0)
            o["description"] = text;
    }
    return o;
}

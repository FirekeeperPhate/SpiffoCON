using System.Globalization;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;

namespace SpiffoCON.Core.Tests;

public class SandboxTests
{
    // shaped like the file a B42 server writes (comments in the server's language, CRLF)
    const string Sample =
        "SandboxVars = {\r\n" +
        "    VERSION = 6,\r\n" +
        "    -- Modificando questo valore... Predefinito = Normale\r\n" +
        "    -- 4 = Normale\r\n" +
        "    Zombies = 4,\r\n" +
        "    ZombieVoronoiNoise = true,\r\n" +
        "    FoodLootNew = 0.8,\r\n" +
        "    WorldItemRemovalList = \"Base.Hat, Base.Glasses\",\r\n" +
        "    ZombieLore = {\r\n" +
        "        -- velocità\r\n" +
        "        Speed = 2,\r\n" +
        "        SprinterPercentage = 33,\r\n" +
        "    },\r\n" +
        "    MyMod = {\r\n" +
        "        Enabled = false,\r\n" +
        "        Items = { \"a\", \"b\" },\r\n" +
        "        Label = 'it''s',\r\n" +
        "    },\r\n" +
        "}\r\n";

    [Fact]
    public void Parses_nested_values_and_ignores_comments()
    {
        var file = SandboxVarsFile.Parse(Sample);
        Assert.Equal("4", file.Values["Zombies"].Value);
        Assert.Equal(LuaValueKind.Boolean, file.Values["ZombieVoronoiNoise"].Kind);
        Assert.Equal("0.8", file.Values["FoodLootNew"].Value);
        Assert.Equal(("Base.Hat, Base.Glasses", LuaValueKind.String), (file.Values["WorldItemRemovalList"].Value, file.Values["WorldItemRemovalList"].Kind));
        Assert.Equal("2", file.Values["ZombieLore.Speed"].Value);
        Assert.Equal("false", file.Values["MyMod.Enabled"].Value);
        Assert.False(file.Values.ContainsKey("MyMod.Items"));
    }

    [Fact]
    public void Writes_only_the_changed_values()
    {
        var file = SandboxVarsFile.Parse(Sample);
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("it-IT");
        string text;
        IReadOnlyList<string> missing;
        try
        {
            text = file.With(new Dictionary<string, string>
            {
                ["Zombies"] = "2",
                ["ZombieLore.Speed"] = "1",
                ["FoodLootNew"] = "1,5",
                ["ZombieVoronoiNoise"] = "false",
                ["WorldItemRemovalList"] = "Base.Hat, \"x\"",
                ["NotInFile"] = "1",
            }, out missing);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal(["NotInFile"], missing);
        var expected = Sample
            .Replace("Zombies = 4,", "Zombies = 2,")
            .Replace("Speed = 2,", "Speed = 1,")
            .Replace("FoodLootNew = 0.8,", "FoodLootNew = 1.5,")
            .Replace("ZombieVoronoiNoise = true,", "ZombieVoronoiNoise = false,")
            .Replace("\"Base.Hat, Base.Glasses\"", "\"Base.Hat, \\\"x\\\"\"");
        Assert.Equal(expected, text);
        // and it still parses to the new values
        var again = SandboxVarsFile.Parse(text);
        Assert.Equal("1.5", again.Values["FoodLootNew"].Value);
        Assert.Equal("Base.Hat, \"x\"", again.Values["WorldItemRemovalList"].Value);
    }

    [Fact]
    public void Whole_doubles_keep_a_decimal_point_only_if_they_had_one()
    {
        var file = SandboxVarsFile.Parse("SandboxVars = { A = 1.0, B = 3, }");
        var text = file.With(new Dictionary<string, string> { ["A"] = "2", ["B"] = "4" }, out _);
        Assert.Equal("SandboxVars = { A = 2, B = 4, }", text);
    }

    [Theory]
    [InlineData("Zombies = 4,")]
    [InlineData("SandboxVars = { Zombies = , }")]
    [InlineData("SandboxVars = { Name = \"unterminated }")]
    public void Broken_files_are_reported(string text)
    {
        var ex = Assert.Throws<FormatException>(() => SandboxVarsFile.Parse(text));
        Assert.Contains("line", ex.Message);
    }

    [Fact]
    public void Sandbox_metadata_loads()
    {
        var all = ServerOptionCatalog.SandboxPages.SelectMany(p => p.Options).ToList();
        Assert.True(all.Count > 250);
        var speed = ServerOptionCatalog.FindSandbox("ZombieLore.Speed")!;
        Assert.Equal((ServerOptionType.Enum, "Speed", "4"), (speed.Type, speed.Title, speed.Default));
        Assert.Equal(["Sprinters", "Fast Shamblers", "Shamblers", "Random"], speed.Values);
        Assert.Null(ServerOptionCatalog.FindSandbox("zombielore.speed"));
    }

    [Fact]
    public void Sandbox_path_next_to_the_ini()
    {
        Assert.Equal("/home/c/Zomboid/Server/pzserver_SandboxVars.lua", SandboxFiles.ForServerConfig("/home/c/Zomboid/Server/pzserver.ini"));
        Assert.Equal(@"C:\Z\Server\test_SandboxVars.lua", SandboxFiles.ForServerConfig(@"C:\Z\Server\test.ini"));
    }

    [Fact]
    public async Task Local_store_keeps_encoding_and_line_endings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spiffocon-{Guid.NewGuid():N}_SandboxVars.lua");
        try
        {
            File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(Sample)]);
            var store = new LocalSandboxStore(path);
            var text = await store.ReadAsync();
            Assert.Equal(Sample, text);
            var changed = SandboxVarsFile.Parse(text).With(new Dictionary<string, string> { ["Zombies"] = "3" }, out _);
            await store.WriteAsync(changed);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
            Assert.Equal(changed, System.Text.Encoding.UTF8.GetString(bytes[3..]));
            Assert.Contains("velocità", changed);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

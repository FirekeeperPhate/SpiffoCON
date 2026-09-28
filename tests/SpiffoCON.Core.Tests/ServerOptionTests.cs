using System.Globalization;
using SpiffoCON.Core.Commands;

namespace SpiffoCON.Core.Tests;

public class ServerOptionTests
{
    [Fact]
    public void Bundled_metadata_loads()
    {
        var all = ServerOptionCatalog.Pages.SelectMany(p => p.Options).ToList();
        Assert.True(all.Count >= 140);
        Assert.Equal(all.Count, all.Select(o => o.Name).Distinct().Count());

        var pvp = ServerOptionCatalog.Find("pvp")!;
        Assert.Equal((ServerOptionType.Boolean, "true"), (pvp.Type, pvp.Default));
        var timer = ServerOptionCatalog.Find("SafetyToggleTimer")!;
        Assert.Equal((ServerOptionType.Integer, "0", "1000"), (timer.Type, timer.Min, timer.Max));
        Assert.Equal("The time it takes for a player to enter and leave PVP mode", timer.Description);
        var policy = ServerOptionCatalog.Find("BadWordPolicy")!;
        Assert.Equal(ServerOptionType.Enum, policy.Type);
        Assert.Equal(["ban", "kick", "log"], policy.Values);
        Assert.Equal(ServerOptionType.Text, ServerOptionCatalog.Find("ServerWelcomeMessage")!.Type);

        // per-server random ids: no default to reset to, and a warning
        foreach (var id in new[] { "ResetID", "ServerPlayerID", "Seed" })
        {
            var o = ServerOptionCatalog.Find(id)!;
            Assert.True(o.NoDefault);
            Assert.Equal("", o.Default);
            Assert.NotNull(o.Warning);
        }
    }

    [Fact]
    public void Validation_follows_what_the_server_accepts()
    {
        var timer = ServerOptionCatalog.Find("SafetyToggleTimer")!;
        Assert.Equal("7", ServerOptionCatalog.Validate(timer, " 7 ").Value);
        Assert.False(ServerOptionCatalog.Validate(timer, "5000").Ok);
        Assert.False(ServerOptionCatalog.Validate(timer, "1.5").Ok);

        var policy = ServerOptionCatalog.Find("BadWordPolicy")!;
        Assert.False(ServerOptionCatalog.Validate(policy, "9").Ok); // would make the server throw

        var voice = ServerOptionCatalog.Find("VoiceMinDistance")!;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("it-IT");
        try
        {
            Assert.Equal("12.5", ServerOptionCatalog.Validate(voice, "12,5").Value); // the server ignores commas
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal("false", ServerOptionCatalog.Validate(ServerOptionCatalog.Find("PVP")!, "False").Value);
        Assert.False(ServerOptionCatalog.Validate(ServerOptionCatalog.Find("PVP")!, "maybe").Ok);

        var welcome = ServerOptionCatalog.Find("ServerWelcomeMessage")!;
        Assert.Equal("Hi <LINE> there", ServerOptionCatalog.Validate(welcome, "Hi\r\nthere ").Value);
        Assert.Equal("Hi\nthere", ServerOptionCatalog.ToEditorText(welcome, "Hi <LINE> there"));
        var description = ServerOptionCatalog.Find("PublicDescription")!;
        Assert.Equal("Line one\\nLine two", ServerOptionCatalog.Validate(description, "Line one\nLine two").Value);
        Assert.Equal("Line one\nLine two", ServerOptionCatalog.ToEditorText(description, "Line one\\nLine two"));
        Assert.Contains("Typing \\n will", description.Description);

        // the default welcome message has empty lines; it must survive editor → server unchanged
        Assert.Equal(welcome.Default, ServerOptionCatalog.Validate(welcome, ServerOptionCatalog.ToEditorText(welcome, welcome.Default)).Value);
    }

    [Fact]
    public void Change_replies_from_a_real_server()
    {
        Assert.Equal("changeoption PublicName \"It's 'mine'\"", ServerOptionCatalog.ChangeCommand("PublicName", "It's \"mine\""));
        Assert.Equal(new ChangeResult(ChangeOutcome.Applied, "false"), ServerOptionCatalog.InterpretChange("Option : PVP is now : false", "false"));
        Assert.Equal(new ChangeResult(ChangeOutcome.Rejected, "2"), ServerOptionCatalog.InterpretChange("Option : SafetyToggleTimer is now : 2", "5000"));
        Assert.Equal(new ChangeResult(ChangeOutcome.Applied, "2"), ServerOptionCatalog.InterpretChange("Option : BadWordPolicy is now : 2(kick)", "2"));
        Assert.Equal(new ChangeResult(ChangeOutcome.Applied, "12.5"), ServerOptionCatalog.InterpretChange("Option : VoiceMinDistance is now : 12.5", "12.50"));
        Assert.Equal(new ChangeResult(ChangeOutcome.Applied, ""), ServerOptionCatalog.InterpretChange("Option : PublicName is now : ", ""));
        Assert.Equal(ChangeOutcome.UnknownOption, ServerOptionCatalog.InterpretChange("Option NotAnOption doesn't exist.", "x").Outcome);
    }
}

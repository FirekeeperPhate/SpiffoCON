using System.Text;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;

namespace SpiffoCON.Core.Tests;

/// <summary>Regressions for the bugs found in the review before 0.9.5.</summary>
public sealed class ReviewFixTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spiffocon-review").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_text_option_ending_in_parentheses_is_applied()
    {
        Assert.Equal(new ChangeResult(ChangeOutcome.Applied, "My Server (PvE)"),
            ServerOptionCatalog.InterpretChange("Option : PublicName is now : My Server (PvE)", "My Server (PvE)"));
        // the value itself may say "doesn't exist"
        Assert.Equal(ChangeOutcome.Applied,
            ServerOptionCatalog.InterpretChange("Option : ServerWelcomeMessage is now : Hell doesn't exist", "Hell doesn't exist").Outcome);
        Assert.Equal(ChangeOutcome.UnknownOption, ServerOptionCatalog.InterpretChange("Option Nope doesn't exist.", "1").Outcome);
        // enums still lose their label
        Assert.Equal(new ChangeResult(ChangeOutcome.Applied, "2"), ServerOptionCatalog.InterpretChange("Option : BadWordPolicy is now : 2(kick)", "2"));
    }

    [Fact]
    public void A_player_name_is_not_read_as_an_error()
    {
        Assert.Equal(CommandOutcome.Success, PlayerCommands.Interpret("User InvalidSam kicked.", "InvalidSam"));
        Assert.Equal(CommandOutcome.Failed, PlayerCommands.Interpret("User \"Unknown1\" not found.", "Unknown1"));
        Assert.Equal(CommandOutcome.Success, EventCommands.Interpret("Lightning triggered"));
    }

    static void Write(string dir, string file, string text) => File.AppendAllText(Path.Combine(dir, file), text, new UTF8Encoding(false));

    const string ChatLine = "[28-09-26 13:00:00.000][info] Got message:ChatMessage{chat=General, author='rj', text='hi'}.\n";

    [Fact]
    public async Task A_restart_seen_live_is_read_from_the_start_of_the_new_file()
    {
        Write(_dir, "2026-09-28_12-25_chat.txt", "[28-09-26 12:27:04.544][info] old\n");
        var tail = new LogTail(new LocalLogFolder(_dir), "chat") { InitialBytes = 200 };
        await tail.PollAsync();
        Assert.True(tail.LastPollWasBacklog);
        Write(_dir, "2026-09-28_12-25_chat.txt", "[28-09-26 12:27:05.000][info] new\n");
        await tail.PollAsync();
        Assert.False(tail.LastPollWasBacklog);

        // polled a moment ago: the new file is news, all of it (mod errors at start-up)
        Write(_dir, "2026-09-28_13-00_chat.txt", string.Concat(Enumerable.Repeat(ChatLine, 20)));
        var lines = await tail.PollAsync();
        Assert.False(tail.LastPollWasBacklog);
        Assert.Equal(20, lines.Count(l => l.Kind == LogLineKind.Chat));
    }

    [Fact]
    public async Task A_restart_during_a_long_gap_is_backlog_not_news()
    {
        Write(_dir, "2026-09-28_12-25_chat.txt", "[28-09-26 12:27:04.544][info] old\n");
        // every gap counts as long
        var tail = new LogTail(new LocalLogFolder(_dir), "chat") { InitialBytes = 200, FreshFileWindow = TimeSpan.Zero };
        await tail.PollAsync();
        Write(_dir, "2026-09-28_13-00_chat.txt", string.Concat(Enumerable.Repeat(ChatLine, 20)));
        var lines = await tail.PollAsync();
        Assert.True(tail.LastPollWasBacklog);
        Assert.True(lines.Count(l => l.Kind == LogLineKind.Chat) < 20);
    }

    [Fact]
    public void Nested_comments_in_mod_scripts_end_where_the_game_ends_them()
    {
        var root = SpiffoCON.Core.Catalog.ScriptParser.Parse(
            "module Base {\n/*\nitem Old {\n Weight = 1, /* kg */\n}\n*/\nitem New { DisplayName = New, }\n}");
        var module = Assert.Single(root.Children);
        var item = Assert.Single(module.Children);
        Assert.Equal(("item", "New"), (item.Type, item.Name));
    }

    [Fact]
    public void Deeply_nested_input_does_not_overflow_the_stack()
    {
        var deep = new string('{', 50_000);
        SpiffoCON.Core.Catalog.ScriptParser.Parse(deep);
        Assert.Throws<FormatException>(() => SandboxVarsFile.Parse("SandboxVars = " + deep));
        Assert.Empty(SpiffoCON.Core.Steam.WorkshopManifest.Parse(string.Concat(Enumerable.Repeat("\"a\" {", 50_000))));
    }

    [Fact]
    public void Odd_log_names_and_manifest_boms_are_tolerated()
    {
        Assert.Null(LogFileInfo.FromName("2026-13-45_10-00_chat.txt", 1));
        Assert.NotNull(LogFileInfo.FromName("2026-09-28_10-00_chat.txt", 1));
        var manifest = "﻿\"AppWorkshop\" { \"WorkshopItemsInstalled\" { \"1\" { \"timeupdated\" \"5\" } } }";
        Assert.Equal(5, SpiffoCON.Core.Steam.WorkshopManifest.Parse(manifest)["1"].TimeUpdated);
    }

    [Fact]
    public void A_legacy_translation_missing_its_quote_keeps_the_next_entry()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "EN")).FullName;
        File.WriteAllText(Path.Combine(folder, "ItemName_EN.txt"),
            "ItemName_EN = {\n ItemName_Base.A = \"Aaa,\n ItemName_Base.B = \"Bbb\",\n}");
        var t = new SpiffoCON.Core.Catalog.Translations();
        t.LoadFolder(folder);
        Assert.Equal("Bbb", t.ItemNames["Base.B"]);
    }

    [Fact]
    public void Broadcasts_in_the_log_read_as_players_see_them()
    {
        var line = LogTail.Parse("[29-09-26 10:03:30.200][info] Server alert message: '<RGB:1,0.6,0>Restart at 20:00 <LINE> <RGB:0.3,0.9,0.3>Be ready' sent..");
        Assert.Equal((LogLineKind.Alert, "Restart at 20:00 / Be ready"), (line.Kind, line.Text));
    }

    [Fact]
    public void A_name_inside_an_error_phrase_does_not_hide_it()
    {
        // a player called "User" and the reply "No such user"
        Assert.Equal(CommandOutcome.Failed, PlayerCommands.Interpret("No such user", "User"));
        Assert.Equal(CommandOutcome.Failed, PlayerCommands.InterpretAddItem("No such user", "t"));
    }

    [Fact]
    public async Task A_sandbox_file_that_is_not_UTF8_is_not_edited()
    {
        var path = Path.Combine(_dir, "x_SandboxVars.lua");
        await File.WriteAllBytesAsync(path, [.. "SandboxVars = { Name = \""u8, 0xE9, .. "\" }"u8]);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LocalSandboxStore(path).ReadAsync());
    }
}

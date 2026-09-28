using System.Text;
using SpiffoCON.Core.Files;

namespace SpiffoCON.Core.Tests;

public sealed class LogTailTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spiffocon-logs").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    void Append(string file, string text) => File.AppendAllText(Path.Combine(_dir, file), text, new UTF8Encoding(false));

    [Fact]
    public void Parses_real_line_formats()
    {
        var chat = LogTail.Parse("[28-09-26 12:27:54.518][info] Got message:ChatMessage{chat=General, author='rj', text='ciao, è tutto ok? {sì}'}.");
        Assert.Equal((LogLineKind.Chat, "General", "rj", "ciao, è tutto ok? {sì}"), (chat.Kind, chat.Channel, chat.Author, chat.Text));
        Assert.Equal(new DateTime(2026, 9, 28, 12, 27, 54, 518), chat.Time);
        Assert.Equal("info", chat.Level);

        var alert = LogTail.Parse("[28-09-26 12:27:54.615] Server alert message: 'Accenti: è à' sent..");
        Assert.Equal((LogLineKind.Alert, "Accenti: è à"), (alert.Kind, alert.Text));

        var admin = LogTail.Parse("[28-09-26 12:27:55.685][IMPORTANT] System banned user ghost1test ban.");
        Assert.Equal((LogLineKind.Server, "IMPORTANT", "System banned user ghost1test ban."), (admin.Kind, admin.Level, admin.Text));

        Assert.Equal(LogLineKind.Server, LogTail.Parse("no prefix at all").Kind);
    }

    [Fact]
    public async Task Follows_the_file_and_switches_on_restart()
    {
        Append("2026-09-28_12-25_chat.txt", "[28-09-26 12:27:04.544][info] Start chat server initialization....\r\n");
        Append("2026-09-28_12-25_admin.txt", "[28-09-26 12:27:55.321] admin kicked user nobody.\r\n");
        var tail = new LogTail(new LocalLogFolder(_dir), "chat");

        var first = await tail.PollAsync();
        Assert.Equal("2026-09-28_12-25_chat.txt", tail.CurrentFile);
        Assert.Single(first);
        Assert.Empty(await tail.PollAsync());

        // a line written in two pieces is returned once complete
        Append("2026-09-28_12-25_chat.txt", "[28-09-26 12:30:00.000][info] Got message:ChatMessage{chat=General, author='rj', text='hel");
        Assert.Empty(await tail.PollAsync());
        Append("2026-09-28_12-25_chat.txt", "lo'}.\r\n");
        var chat = Assert.Single(await tail.PollAsync());
        Assert.Equal("hello", chat.Text);

        // server restart: new file
        Append("2026-09-28_13-08_chat.txt", "[28-09-26 13:08:01.000][info] Start chat server initialization....\r\n");
        var after = await tail.PollAsync();
        Assert.Equal(LogLineKind.Marker, after[0].Kind);
        Assert.Equal(2, after.Count);
        Assert.Equal("2026-09-28_13-08_chat.txt", tail.CurrentFile);
    }

    [Fact]
    public async Task Starts_near_the_end_of_a_big_file_without_a_cut_line()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++)
            sb.Append($"[28-09-26 12:00:00.000][info] line number {i:D5} with some padding text\r\n");
        Append("2026-09-28_12-00_chat.txt", sb.ToString());
        var tail = new LogTail(new LocalLogFolder(_dir), "chat") { InitialBytes = 4096 };

        var lines = await tail.PollAsync();
        Assert.InRange(lines.Count, 40, 70);
        Assert.All(lines, l => Assert.StartsWith("line number", l.Text));
        Assert.Equal("line number 04999 with some padding text", lines[^1].Text);
    }

    [Fact]
    public void Log_file_names()
    {
        var info = LogFileInfo.FromName("2026-09-28_13-08_DebugLog-server.txt", 10)!;
        Assert.Equal(("DebugLog-server", new DateTime(2026, 9, 28, 13, 8, 0)), (info.Type, info.Started));
        Assert.Null(LogFileInfo.FromName("notes.txt", 1));
    }
}

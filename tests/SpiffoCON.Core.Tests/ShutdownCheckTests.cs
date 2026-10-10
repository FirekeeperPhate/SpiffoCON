using SpiffoCON.Core.Files;

namespace SpiffoCON.Core.Tests;

public sealed class ShutdownCheckTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "spiffocon-shutdown-" + Guid.NewGuid().ToString("N"));

    public ShutdownCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    void Write(string name, string text)
    {
        var path = Path.Combine(_dir, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    const string Running = "[10-10-26 09:45:02.100] LOG  : General      f:0 st:1> RCON: listening on port 27015.\n";

    // as the server writes when it is told to quit
    const string CleanEnd = """
        [10-10-26 09:49:19.400] LOG  : General      f:783 st:148> Saving finish.
        [10-10-26 09:49:19.534] LOG  : General      f:783 st:148> waiting for UdpEngine thread termination.
        [10-10-26 09:49:19.596] LOG  : General      f:783 st:148> Shutdown handling finished.

        """;

    [Fact]
    public async Task A_run_that_ended_with_the_shutdown_lines_was_stopped_as_it_should()
    {
        Write("2026-10-10_09-49_DebugLog-server.txt", Running);
        Write("logs_2026-10-10/2026-10-10_09-45_DebugLog-server.txt", Running + CleanEnd);
        Write("logs_2026-10-10/2026-10-10_09-45_chat.txt", "chat\n");

        var run = await ShutdownCheck.CheckAsync(new LocalLogFolder(_dir));
        Assert.NotNull(run);
        Assert.True(run.Clean);
        Assert.Equal(("logs_2026-10-10/2026-10-10_09-45_DebugLog-server.txt", "2026-10-10_09-49_DebugLog-server.txt"), (run.File, run.CurrentFile));
        Assert.Equal(new DateTime(2026, 10, 10, 9, 45, 0), run.Started);
        Assert.Equal(new DateTime(2026, 10, 10, 9, 49, 19, 596), run.Ended);
    }

    [Fact]
    public async Task A_run_whose_log_just_stops_crashed_or_was_killed()
    {
        Write("2026-10-10_12-00_DebugLog-server.txt", Running);
        // an older run that ended well, in an older folder, and the last one, which did not
        Write("logs_2026-10-09/2026-10-09_20-00_DebugLog-server.txt", Running + CleanEnd);
        Write("logs_2026-10-10/2026-10-10_08-00_DebugLog-server.txt", Running + CleanEnd);
        Write("logs_2026-10-10/2026-10-10_09-45_DebugLog-server.txt",
            Running + "[10-10-26 11:58:40.001] LOG  : General      f:9 st:9> zombie count 412.\n[10-10-26 11:58:41.250] ERROR: General      f:9 st:9> java.lang.OutOfMemoryError: Java heap space\n");

        var run = await ShutdownCheck.CheckAsync(new LocalLogFolder(_dir));
        Assert.NotNull(run);
        Assert.False(run.Clean);
        Assert.Equal("logs_2026-10-10/2026-10-10_09-45_DebugLog-server.txt", run.File);
        Assert.Equal(new DateTime(2026, 10, 10, 11, 58, 41, 250), run.Ended);
        Assert.Contains("OutOfMemoryError", run.LastLines[^1]);
    }

    [Fact]
    public async Task A_long_log_is_judged_by_its_end()
    {
        Write("2026-10-10_12-00_DebugLog-server.txt", Running);
        // the shutdown lines of an earlier day's text in the middle of the file do not count: only the end is read
        var filler = string.Concat(Enumerable.Repeat("[10-10-26 10:00:00.000] LOG  : General      f:1 st:1> a line like many others in a busy server log.\n", 400));
        Write("logs_2026-10-10/2026-10-10_09-45_DebugLog-server.txt", Running + CleanEnd + filler);
        Assert.False((await ShutdownCheck.CheckAsync(new LocalLogFolder(_dir)))!.Clean);

        Write("logs_2026-10-10/2026-10-10_09-45_DebugLog-server.txt", Running + filler + CleanEnd);
        Assert.True((await ShutdownCheck.CheckAsync(new LocalLogFolder(_dir)))!.Clean);
    }

    [Fact]
    public async Task Nothing_to_judge_without_a_run_going_on_or_an_earlier_one()
    {
        // no run going on: the last log is still in the folder, nobody moved it yet
        Assert.Null(await ShutdownCheck.CheckAsync(new LocalLogFolder(_dir)));
        Write("2026-10-10_09-49_chat.txt", "chat\n");
        Assert.Null(await ShutdownCheck.CheckAsync(new LocalLogFolder(_dir)));

        // the first run ever: nothing was put away
        Write("2026-10-10_09-49_DebugLog-server.txt", Running);
        Assert.Null(await ShutdownCheck.CheckAsync(new LocalLogFolder(_dir)));

        // a folder that is not the server's, and a log put away that is not the debug log
        Write("notes/2026-10-10_09-00_DebugLog-server.txt", Running);
        Write("logs_2026-10-10/2026-10-10_09-45_chat.txt", "chat\n");
        Assert.Null(await ShutdownCheck.CheckAsync(new LocalLogFolder(_dir)));
    }
}

using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Steam;

namespace SpiffoCON.Core.Tests;

public class MaintenanceTests
{
    static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    [Fact]
    public void Compares_the_server_copies_with_Steam()
    {
        var installed = new Dictionary<string, InstalledWorkshopItem>
        {
            ["1"] = new("1", 1000, "m1"),
            ["2"] = new("2", 2000, "m2"),
            ["3"] = new("3", 3000, "m3"),
        };
        var steam = new Dictionary<string, WorkshopItemInfo>
        {
            ["1"] = new("1", "Brita", At(1000), 10, true),
            ["2"] = new("2", "Arsenal", At(2500), 10, true),
            ["3"] = new("3", "3", default, 0, false), // unlisted: Steam's API says result 9
            ["4"] = new("4", "Fresh mod", At(100), 10, true),
        };

        var rows = ModUpdates.Compare(["1", "2", "3", "4", "2"], installed, steam);

        Assert.Equal(["2", "4", "3", "1"], rows.Select(r => r.WorkshopId));
        Assert.Equal(ModUpdateState.UpdateAvailable, rows[0].State);
        Assert.Equal("Arsenal", rows[0].Title);
        Assert.Equal(At(2000), rows[0].OnServer);
        Assert.Equal(At(2500), rows[0].OnSteam);
        Assert.Equal(ModUpdateState.NotOnServer, rows[1].State);
        Assert.Equal(ModUpdateState.Unknown, rows[2].State);
        Assert.Equal(ModUpdateState.UpToDate, rows[3].State);
    }

    [Fact]
    public void Warns_at_the_start_then_at_round_times()
    {
        Assert.Equal([300, 180, 120, 60, 30, 10], RestartCountdown.WarningsFor(300));
        Assert.Equal([60, 30, 10], RestartCountdown.WarningsFor(60));
        Assert.Equal([3600, 2700, 1800, 1200, 900, 600, 300, 180, 120, 60, 30, 10], RestartCountdown.WarningsFor(3600));
    }

    [Theory]
    [InlineData(10, "10 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(299, "5 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(5400, "1 hour 30 minutes")]
    public void Describes_the_time_left(int seconds, string expected)
    {
        Assert.Equal(expected, RestartCountdown.Describe(seconds));
    }

    [Fact]
    public void Fills_in_the_message()
    {
        Assert.Equal("Restart in 2 minutes!", RestartCountdown.Message("Restart in {TIME}!", 120));
        Assert.Equal("Restarting soon (30 seconds)", RestartCountdown.Message("Restarting soon", 30));
        Assert.Contains("10 minutes", RestartCountdown.Message(" ", 600));
    }

    [Fact]
    public async Task Reads_the_manifest_over_the_remote_file_system()
    {
        var root = Path.Combine(Path.GetTempPath(), "spiffocon-acf-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = Path.Combine(root, "steamapps", "workshop", "content", "108600");
            Directory.CreateDirectory(content);
            File.WriteAllText(Path.Combine(root, "steamapps", "workshop", WorkshopManifest.FileName),
                "\"AppWorkshop\" { \"WorkshopItemsInstalled\" { \"42\" { \"timeupdated\" \"77\" \"manifest\" \"9\" } } }");
            using var fs = new LocalRemoteFileSystem(new LocalRemoteFileSystem.Counters());

            var items = await WorkshopManifest.ReadAsync(fs, content.Replace('\\', '/'));
            Assert.Equal(77, items!["42"].TimeUpdated);
            Assert.Null(await WorkshopManifest.ReadAsync(fs, root.Replace('\\', '/') + "/nothing/content/108600"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

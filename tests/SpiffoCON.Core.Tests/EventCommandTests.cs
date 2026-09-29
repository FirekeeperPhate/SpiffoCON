using System.Globalization;
using SpiffoCON.Core.Commands;

namespace SpiffoCON.Core.Tests;

public class EventCommandTests
{
    static WorldEvent Event(string id) => EventCommands.Events.Single(e => e.Id == id);

    [Fact]
    public void Rain_intensity_is_kept_between_1_and_100()
    {
        // the B42 server takes 0 and 150 too ("Rain started"), so SpiffoCON clamps
        Assert.Equal("startrain 1", EventCommands.StartRain(0));
        Assert.Equal("startrain 60", EventCommands.StartRain(60));
        Assert.Equal("startrain 100", EventCommands.StartRain(150));
    }

    [Fact]
    public void Storm_duration_is_whole_game_hours_in_any_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("it-IT");
            Assert.Equal("startstorm 3", EventCommands.StartStorm(2.6));
            Assert.Equal("startstorm 1", EventCommands.StartStorm(0));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Player_events_quote_the_username()
    {
        Assert.Equal("lightning \"Rick 'G'\"", EventCommands.Build(Event("lightning"), "Rick \"G\""));
        Assert.Equal("thunder \"rj\"", EventCommands.Build(Event("thunder"), " rj "));
        Assert.Equal("createhorde 30 \"rj\"", EventCommands.Build(Event("horde"), "rj", 30));
        Assert.Equal("createhorde 500 \"rj\"", EventCommands.Build(Event("horde"), "rj", 9000));
    }

    [Fact]
    public void Server_picked_events_take_no_player()
    {
        // B42: "chopper <word>" is start/stop, anything else only returns the help text
        Assert.Equal("chopper", EventCommands.Build(Event("chopper"), "rj"));
        Assert.Equal("gunshot", EventCommands.Build(Event("gunshot"), null));
    }

    [Fact]
    public void Player_events_need_a_player()
    {
        Assert.Throws<ArgumentException>(() => EventCommands.Build(Event("lightning"), " "));
    }

    [Theory]
    // replies of a real B42 dedicated server
    [InlineData("Rain started", CommandOutcome.Success)]
    [InlineData("Thunderstorm started", CommandOutcome.Success)]
    [InlineData("Weather stopped", CommandOutcome.Success)]
    [InlineData("Lightning triggered", CommandOutcome.Success)]
    [InlineData("Chopper launched", CommandOutcome.Success)]
    [InlineData("Chopper deactivated", CommandOutcome.Success)]
    [InlineData("Gunshot fired", CommandOutcome.Success)]
    [InlineData("Horde spawned.", CommandOutcome.Success)]
    [InlineData("User \"nobody\" not found", CommandOutcome.Failed)]
    [InlineData("Pass a username", CommandOutcome.Failed)]
    [InlineData("Specify a player to create the horde near to.", CommandOutcome.Failed)]
    // createhorde2 and removezombies on a real B42 server (no player near: the area is not loaded)
    [InlineData("invalid location", CommandOutcome.Failed)]
    [InlineData("Zombies removed.", CommandOutcome.Success)]
    [InlineData("Inizia a piovere sul server. Usa /startrain \"intensità\"", CommandOutcome.Unconfirmed)]
    public void Interprets_replies(string reply, CommandOutcome expected)
    {
        Assert.Equal(expected, EventCommands.Interpret(reply));
    }

    [Fact]
    public void Zombies_are_placed_and_removed_at_a_square()
    {
        Assert.Equal("createhorde2 -x 10640 -y 9560 -z 0 -count 500 -radius 5", EventCommands.HordeAt(10640, 9560, 0, 900, 5));
        Assert.Equal("createhorde2 -x 1 -y 2 -z 0 -count 1 -radius 1", EventCommands.HordeAt(1, 2, 0, 0, 0));
        Assert.Equal("removezombies -x 10640 -y 9560 -z 0 -radius 50", EventCommands.RemoveZombiesAt(10640, 9560, 0, 80));
    }
}

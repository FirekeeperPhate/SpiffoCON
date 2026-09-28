using System.Globalization;
using SpiffoCON.Core.Commands;

namespace SpiffoCON.Core.Tests;

public class ServerMessageTests
{
    [Fact]
    public void Quote_replaces_inner_quotes_and_line_breaks()
    {
        Assert.Equal("\"say 'hi' now\"", CommandText.Quote("say \"hi\"\r\nnow"));
    }

    [Fact]
    public void Lines_become_LINE_tags_and_blank_edges_are_dropped()
    {
        Assert.Equal("Restart in 5 minutes <LINE> Save your stuff",
            ServerMessage.ToMarkup("\n  \nRestart in 5 minutes   \r\nSave your stuff\n\n"));
    }

    [Fact]
    public void Builds_a_quoted_servermsg()
    {
        Assert.Equal("servermsg \"Hello, it's \\ fine <LINE> Bye\"",
            ServerMessage.BuildCommand("Hello, it\"s \\ fine\nBye"));
    }

    [Fact]
    public void Color_tag_uses_dots_in_an_italian_locale()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("it-IT");
        try
        {
            Assert.Equal("<RGB:1,0.5,0>", new RgbColor(1f, 0.5f, 0f).ToTag());
            Assert.Equal("<RGB:0.2,0.4,1>", RgbColor.FromBytes(51, 102, 255).ToTag());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("Message sent.", CommandOutcome.Success)]
    [InlineData("message sent\n", CommandOutcome.Success)]
    [InlineData("", CommandOutcome.Unconfirmed)]
    [InlineData("Something new in B42.21", CommandOutcome.Unconfirmed)]
    [InlineData("Unknown command servermsg", CommandOutcome.Failed)]
    public void Interprets_replies(string reply, CommandOutcome expected)
    {
        Assert.Equal(expected, ServerMessage.Interpret(reply));
    }

    [Fact]
    public void Preview_carries_colors_across_lines()
    {
        var lines = ServerMessage.Preview("Hi <RGB:1,0,0>red\nstill red <RGB:0,1,0> green", RgbColor.White);

        Assert.Equal(2, lines.Count);
        Assert.Equal([new MessageSegment("Hi ", RgbColor.White), new MessageSegment("red", new RgbColor(1, 0, 0))], lines[0]);
        Assert.Equal([new MessageSegment("still red ", new RgbColor(1, 0, 0)), new MessageSegment(" green", new RgbColor(0, 1, 0))], lines[1]);
    }
}

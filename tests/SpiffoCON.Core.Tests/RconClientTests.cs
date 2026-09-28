using SpiffoCON.Core.Rcon;

namespace SpiffoCON.Core.Tests;

public class RconClientTests
{
    static RconOptions Fast => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        CommandTimeout = TimeSpan.FromSeconds(3),
    };

    [Fact]
    public async Task Connects_and_executes()
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Fast);
        await client.ConnectAsync("127.0.0.1", server.Port, "secret");

        Assert.True(client.IsConnected);
        Assert.Equal("echo:players", await client.ExecuteAsync("players"));
    }

    [Fact]
    public async Task Wrong_password_is_reported()
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Fast);

        await Assert.ThrowsAsync<RconAuthenticationException>(() => client.ConnectAsync("127.0.0.1", server.Port, "nope"));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Joins_a_reply_split_over_several_packets()
    {
        var big = string.Concat(Enumerable.Range(0, 3000).Select(i => $"{i % 10}"));
        big += big + big; // 9000 chars
        await using var server = new FakeRconServer { ChunkSize = 4000, Handler = _ => big };
        await using var client = new RconClient(Fast);
        await client.ConnectAsync("127.0.0.1", server.Port, "secret");

        Assert.Equal(big, await client.ExecuteAsync("showoptions"));
    }

    [Fact]
    public async Task Accepts_one_oversized_packet()
    {
        var big = new string('x', 20_000);
        await using var server = new FakeRconServer { Handler = _ => big };
        await using var client = new RconClient(Fast);
        await client.ConnectAsync("127.0.0.1", server.Port, "secret");

        Assert.Equal(big, await client.ExecuteAsync("showoptions"));
    }

    [Fact]
    public async Task Late_reply_is_not_returned_for_the_next_command()
    {
        await using var server = new FakeRconServer
        {
            Delay = cmd => cmd == "slow" ? TimeSpan.FromMilliseconds(800) : TimeSpan.Zero,
        };
        await using var client = new RconClient(Fast with { CommandTimeout = TimeSpan.FromMilliseconds(300) });
        await client.ConnectAsync("127.0.0.1", server.Port, "secret");

        await Assert.ThrowsAsync<RconTimeoutException>(() => client.ExecuteAsync("slow"));
        await Task.Delay(700); // the late "echo:slow" is now queued
        Assert.Equal("echo:fast", await client.ExecuteAsync("fast"));
    }

    [Fact]
    public async Task Reconnects_after_the_server_drops_the_connection()
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Fast);
        var lost = new TaskCompletionSource();
        client.ConnectionLost += (_, _) => lost.TrySetResult();
        await client.ConnectAsync("127.0.0.1", server.Port, "secret");

        server.DropClients();
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(client.IsConnected);

        Assert.Equal("echo:save", await client.ExecuteAsync("save"));
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task Round_trips_non_ascii_text()
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Fast);
        await client.ConnectAsync("127.0.0.1", server.Port, "secret");

        Assert.Equal("echo:servermsg \"Perché è così?\"", await client.ExecuteAsync("servermsg \"Perché è così?\""));
    }

    [Fact]
    public async Task Rejects_commands_longer_than_the_protocol_allows()
    {
        await using var server = new FakeRconServer();
        await using var client = new RconClient(Fast);
        await client.ConnectAsync("127.0.0.1", server.Port, "secret");

        await Assert.ThrowsAsync<RconException>(() => client.ExecuteAsync(new string('a', 5000)));
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task Unreachable_port_fails_cleanly()
    {
        await using var client = new RconClient(Fast);
        await Assert.ThrowsAsync<RconException>(() => client.ConnectAsync("127.0.0.1", 1, "secret"));
    }
}

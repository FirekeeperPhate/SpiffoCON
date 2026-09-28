using System.Net;
using System.Net.Sockets;
using System.Text;
using SpiffoCON.Core.Rcon;

namespace SpiffoCON.Core.Tests;

/// <summary>Minimal Source RCON server on loopback for tests.</summary>
sealed class FakeRconServer : IAsyncDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly List<TcpClient> _clients = [];
    readonly Task _acceptLoop;
    static readonly Encoding Utf8 = new UTF8Encoding(false);

    public string Password { get; init; } = "secret";

    /// <summary>Command → reply. Return null to send nothing.</summary>
    public Func<string, string?> Handler { get; set; } = cmd => $"echo:{cmd}";

    /// <summary>Split replies into chunks of this many bytes (0 = one packet).</summary>
    public int ChunkSize { get; set; }

    /// <summary>Delay before replying to a command.</summary>
    public Func<string, TimeSpan> Delay { get; set; } = _ => TimeSpan.Zero;

    public List<string> Received { get; } = [];
    public int Connections;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeRconServer()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Drops every open connection, like a host restarting.</summary>
    public void DropClients()
    {
        lock (_clients)
        {
            foreach (var c in _clients)
                c.Dispose();
            _clients.Clear();
        }
    }

    async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref Connections);
                lock (_clients)
                    _clients.Add(client);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch { }
    }

    async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var writeLock = new SemaphoreSlim(1, 1);
            while (true)
            {
                var packet = await RconPacket.ReadAsync(stream, Utf8, 1 << 20, _stop.Token);
                if (packet.Type == RconPacket.TypeAuth)
                {
                    bool ok = packet.Body == Password;
                    await Send(stream, writeLock, packet.Id, RconPacket.TypeResponseValue, "");
                    await Send(stream, writeLock, ok ? packet.Id : -1, RconPacket.TypeAuthResponse, "");
                    continue;
                }

                lock (Received)
                    Received.Add(packet.Body);
                var reply = Handler(packet.Body);
                if (reply is null)
                    continue;
                var delay = Delay(packet.Body);
                // replies run concurrently so a slow one doesn't hold back later ones
                _ = Task.Run(async () =>
                {
                    if (delay > TimeSpan.Zero)
                        await Task.Delay(delay);
                    foreach (var chunk in Chunks(reply))
                        await Send(stream, writeLock, packet.Id, RconPacket.TypeResponseValue, chunk);
                });
            }
        }
        catch { }
    }

    IEnumerable<string> Chunks(string reply)
    {
        if (ChunkSize <= 0 || reply.Length <= ChunkSize)
        {
            yield return reply;
            yield break;
        }
        for (int i = 0; i < reply.Length; i += ChunkSize)
            yield return reply.Substring(i, Math.Min(ChunkSize, reply.Length - i));
    }

    async Task Send(NetworkStream stream, SemaphoreSlim writeLock, int id, int type, string body)
    {
        var bytes = RconPacket.Encode(id, type, body, Utf8);
        await writeLock.WaitAsync();
        try { await stream.WriteAsync(bytes); }
        finally { writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        DropClients();
        try { await _acceptLoop; } catch { }
    }
}

using System.Net.Sockets;
using System.Threading.Channels;

namespace SpiffoCON.Core.Rcon;

/// <summary>
/// One TCP connection. A background loop reads packets into a channel, so waiting for a reply
/// can time out without ever cancelling a socket read halfway through a packet.
/// </summary>
internal sealed class RconConnection : IAsyncDisposable
{
    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly RconOptions _options;
    readonly Channel<RconPacket> _incoming = Channel.CreateUnbounded<RconPacket>(new() { SingleWriter = true });
    readonly CancellationTokenSource _shutdown = new();
    readonly Task _readLoop;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    volatile bool _disposing;

    /// <summary>Raised once, from the read loop, when the connection ends without <see cref="DisposeAsync"/>.</summary>
    public event Action<RconConnection, Exception?>? Lost;

    RconConnection(TcpClient tcp, RconOptions options)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _options = options;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public bool IsAlive => !_readLoop.IsCompleted;

    public static async Task<RconConnection> OpenAsync(string host, int port, RconOptions options, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ConnectTimeout);
        try
        {
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new RconException($"Timed out connecting to {host}:{port}.");
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new RconException($"Cannot connect to {host}:{port}: {ex.Message}", ex);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return new RconConnection(tcp, options);
    }

    public async Task SendAsync(int id, int type, string body, CancellationToken ct)
    {
        var bytes = RconPacket.Encode(id, type, body, _options.Encoding);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new RconConnectionLostException("Connection lost while sending.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Next packet, or null if none arrives within <paramref name="timeout"/>.</summary>
    public async Task<RconPacket?> ReceiveAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            return await _incoming.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException ex)
        {
            var cause = ex.InnerException;
            throw new RconConnectionLostException(
                cause is null or EndOfStreamException ? "The server closed the connection." : $"Connection lost: {cause.Message}",
                cause);
        }
    }

    async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                var packet = await RconPacket.ReadAsync(_stream, _options.Encoding, _options.MaxPacketSize, _shutdown.Token)
                    .ConfigureAwait(false);
                _incoming.Writer.TryWrite(packet);
            }
        }
        catch (Exception ex)
        {
            error = _disposing ? null : ex;
        }
        _incoming.Writer.TryComplete(error);
        if (!_disposing)
            Lost?.Invoke(this, error);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposing)
            return;
        _disposing = true;
        _shutdown.Cancel();
        _tcp.Dispose();
        try { await _readLoop.ConfigureAwait(false); } catch { /* already reported through the channel */ }
        _shutdown.Dispose();
    }
}

using System.Text;

namespace SpiffoCON.Core.Rcon;

/// <summary>
/// Source RCON client tuned for Project Zomboid. Commands run one at a time; replies are matched
/// by request id, so a late reply to a timed-out command is discarded instead of being returned
/// for the next one.
/// </summary>
public sealed class RconClient(RconOptions? options = null) : IAsyncDisposable
{
    readonly RconOptions _options = options ?? new RconOptions();
    readonly SemaphoreSlim _gate = new(1, 1);
    RconConnection? _connection;
    string? _host;
    int _port;
    string? _password;
    int _lastId;

    /// <summary>Raised (on a background thread) when an established connection drops.</summary>
    public event EventHandler<Exception?>? ConnectionLost;

    public bool IsConnected => _connection?.IsAlive == true;

    public RconOptions Options => _options;

    public async Task ConnectAsync(string host, int port, string password, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            _connection = await OpenAuthenticatedAsync(host, port, password, ct).ConfigureAwait(false);
            _host = host;
            _port = port;
            _password = password;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCoreAsync().ConfigureAwait(false);
            _host = null;
            _password = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs a console command and returns the server's reply text.</summary>
    public async Task<string> ExecuteAsync(string command, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        int bytes = _options.Encoding.GetByteCount(command);
        if (bytes > _options.MaxCommandBytes)
            throw new RconException($"Command is {bytes} bytes; RCON allows at most {_options.MaxCommandBytes}.");

        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); // closed while this command waited for the gate
            var connection = await EnsureConnectedAsync(ct).ConfigureAwait(false);
            int id = NextId();
            await connection.SendAsync(id, RconPacket.TypeExecCommand, command, ct).ConfigureAwait(false);

            RconPacket? packet;
            var deadline = DateTime.UtcNow + _options.CommandTimeout;
            do
            {
                var left = deadline - DateTime.UtcNow;
                packet = left > TimeSpan.Zero ? await connection.ReceiveAsync(left, ct).ConfigureAwait(false) : null;
                if (packet is null)
                    throw new RconTimeoutException($"No reply within {_options.CommandTimeout.TotalSeconds:0} s. The command may still run on the server.");
            }
            while (packet.Value.Id != id); // stale reply to an earlier, timed-out command

            var reply = new StringBuilder(packet.Value.Body);
            while (_options.Encoding.GetByteCount(packet.Value.Body) >= _options.SplitThreshold)
            {
                packet = await connection.ReceiveAsync(_options.FragmentWait, ct).ConfigureAwait(false);
                if (packet is null || packet.Value.Id != id)
                    break;
                reply.Append(packet.Value.Body);
            }
            return reply.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    async Task<RconConnection> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_connection is { IsAlive: true })
            return _connection;
        if (_host is null || _password is null)
            throw new RconException("Not connected.");
        if (!_options.AutoReconnect)
            throw new RconConnectionLostException("The connection was lost.", null);

        await CloseCoreAsync().ConfigureAwait(false);
        _connection = await OpenAuthenticatedAsync(_host, _port, _password, ct).ConfigureAwait(false);
        return _connection;
    }

    async Task<RconConnection> OpenAuthenticatedAsync(string host, int port, string password, CancellationToken ct)
    {
        var connection = await RconConnection.OpenAsync(host, port, _options, ct).ConfigureAwait(false);
        try
        {
            int id = NextId();
            await connection.SendAsync(id, RconPacket.TypeAuth, password, ct).ConfigureAwait(false);

            // Source servers send an empty RESPONSE_VALUE before the AUTH_RESPONSE; accept either order.
            var deadline = DateTime.UtcNow + _options.ConnectTimeout;
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                RconPacket? packet;
                try
                {
                    packet = left > TimeSpan.Zero ? await connection.ReceiveAsync(left, ct).ConfigureAwait(false) : null;
                }
                catch (RconConnectionLostException ex)
                {
                    // PZ answers a wrong password with id -1 (checked on a B42 server); a drop here
                    // is a server stopping or starting, not a rejected password
                    throw new RconConnectionLostException($"The server closed the connection during login. {ex.Message}", ex);
                }
                if (packet is null)
                    throw new RconException($"No login reply from {host}:{port}. Is this the RCON port?");
                if (packet.Value.Type != RconPacket.TypeAuthResponse)
                    continue;
                if (packet.Value.Id == -1)
                    throw new RconAuthenticationException("The server rejected the RCON password.");
                if (packet.Value.Id == id)
                    break;
            }

            connection.Lost += OnConnectionLost;
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    void OnConnectionLost(RconConnection connection, Exception? error)
    {
        if (ReferenceEquals(connection, _connection))
            ConnectionLost?.Invoke(this, error);
    }

    async Task CloseCoreAsync()
    {
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            connection.Lost -= OnConnectionLost;
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    int NextId()
    {
        // ids stay positive: -1 means "auth failed"
        int id = Interlocked.Increment(ref _lastId);
        if (id <= 0)
        {
            Interlocked.Exchange(ref _lastId, 1);
            id = 1;
        }
        return id;
    }

    bool _disposed;

    void ThrowIfDisposed()
    {
        if (_disposed)
            throw new RconException("The connection is closed.");
    }

    /// <summary>
    /// Closes the connection; later commands fail with an RconException. The gate is not disposed:
    /// a command already waiting for it (a timer, a click during shutdown) must still get it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }
}

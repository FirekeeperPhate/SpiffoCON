using System.Text;

namespace SpiffoCON.Core.Rcon;

public sealed record RconOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>PZ runs commands on its main loop tick, and "save" can take a while.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A reply at least this long may continue in further packets with the same id;
    /// the client then waits <see cref="FragmentWait"/> for the next one.
    /// </summary>
    public int SplitThreshold { get; init; } = 4000;

    public TimeSpan FragmentWait { get; init; } = TimeSpan.FromMilliseconds(250);

    public int MaxPacketSize { get; init; } = 4 * 1024 * 1024;

    /// <summary>Largest command body the Source protocol allows (4096 minus header).</summary>
    public int MaxCommandBytes { get; init; } = 4086;

    /// <summary>Reconnect transparently before a command when the previous connection dropped.</summary>
    public bool AutoReconnect { get; init; } = true;

    public Encoding Encoding { get; init; } = new UTF8Encoding(false);
}

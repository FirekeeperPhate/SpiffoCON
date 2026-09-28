using System.Buffers.Binary;
using System.Text;

namespace SpiffoCON.Core.Rcon;

/// <summary>
/// One Source RCON packet: int32 size, int32 id, int32 type, null-terminated body, empty string.
/// All integers are little-endian.
/// </summary>
public readonly record struct RconPacket(int Id, int Type, string Body)
{
    public const int TypeResponseValue = 0;
    public const int TypeExecCommand = 2;
    public const int TypeAuthResponse = 2;
    public const int TypeAuth = 3;

    /// <summary>id + type + two null terminators.</summary>
    const int MinSize = 10;

    public static byte[] Encode(int id, int type, string body, Encoding encoding)
    {
        int bodyLength = encoding.GetByteCount(body);
        int size = MinSize + bodyLength;
        var buffer = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), size);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), type);
        encoding.GetBytes(body, buffer.AsSpan(12));
        // the two trailing zero bytes are already there
        return buffer;
    }

    /// <summary>
    /// Reads one packet. Throws <see cref="EndOfStreamException"/> when the peer closes the connection.
    /// The declared size is not capped at the protocol's 4096 bytes: Project Zomboid sends long
    /// replies (showoptions, players) in a single oversized packet.
    /// </summary>
    public static async Task<RconPacket> ReadAsync(Stream stream, Encoding encoding, int maxSize, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < MinSize || size > maxSize)
            throw new RconProtocolException($"Invalid RCON packet size {size}.");

        var data = new byte[size];
        await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        int id = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0));
        int type = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));

        var body = data.AsSpan(8, size - 8);
        int end = body.IndexOf((byte)0);
        if (end >= 0)
            body = body[..end];
        return new RconPacket(id, type, encoding.GetString(body));
    }
}

using System.Buffers.Binary;
using System.Text;

namespace SpiffoCON.Core.Catalog;

/// <summary>A sprite in a texture pack page: its rectangle, and where it sits in its untrimmed size.</summary>
public sealed record PackEntry(string Name, int X, int Y, int Width, int Height, int OffsetX, int OffsetY, int FullWidth, int FullHeight);

/// <summary>One page: a PNG atlas and the sprites cut from it.</summary>
public sealed record PackPage(string Name, bool HasAlpha, IReadOnlyList<PackEntry> Entries, long PngOffset, long PngLength);

/// <summary>
/// Reads the game's media/texturepacks/*.pack files (zombie.fileSystem.TexturePackDevice):
/// optional "PZPK" + version, page count, then per page: name, entry count, alpha flag, entries
/// (name, x, y, w, h, ox, oy, fw, fh) and the page PNG. Version 1 stores the PNG length before it;
/// version 0 ends the PNG with the 0xDEADBEEF marker. Only the game's own files are read: its
/// art is never bundled with SpiffoCON.
/// </summary>
public static class TexturePack
{
    const int MaxCount = 1_000_000;
    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static IReadOnlyList<PackPage> ReadIndex(Stream stream)
    {
        // the files are written little-endian; big-endian is tried if the numbers make no sense
        foreach (var little in new[] { true, false })
        {
            stream.Position = 0;
            try
            {
                return ReadIndex(new Reader(stream, little));
            }
            catch (InvalidDataException) when (little)
            {
            }
        }
        throw new InvalidDataException("Not a texture pack.");
    }

    static IReadOnlyList<PackPage> ReadIndex(Reader r)
    {
        int version = 0;
        var magic = r.Bytes(4);
        if (magic.SequenceEqual("PZPK"u8.ToArray()))
        {
            version = r.Int();
            if (version is < 1 or > 16)
                throw new InvalidDataException($"Unknown texture pack version {version}.");
        }
        else
        {
            r.Position = 0;
        }

        int pages = r.Count();
        var result = new List<PackPage>(pages);
        for (int p = 0; p < pages; p++)
        {
            var name = r.String();
            int count = r.Count();
            bool alpha = r.Int() != 0;
            var entries = new List<PackEntry>(count);
            for (int i = 0; i < count; i++)
            {
                var entryName = r.String();
                entries.Add(new PackEntry(entryName, r.Int(), r.Int(), r.Int(), r.Int(), r.Int(), r.Int(), r.Int(), r.Int()));
            }

            long start, length;
            if (version >= 1)
            {
                length = r.Int();
                if (length < 0 || r.Position + length > r.Length)
                    throw new InvalidDataException("Bad page length.");
                start = r.Position;
                r.Position += length;
            }
            else
            {
                start = r.Position;
                length = PngLength(r, start);
                r.Position = start + length;
                // version 0 follows the PNG with 0xDEADBEEF
                if (r.Position + 4 <= r.Length && r.Bytes(4) is var marker && !marker.SequenceEqual(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE }) && !marker.SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }))
                    r.Position -= 4;
            }
            result.Add(new PackPage(name, alpha, entries, start, length));
        }
        return result;
    }

    /// <summary>Walks the PNG chunks up to IEND.</summary>
    static long PngLength(Reader r, long start)
    {
        r.Position = start;
        if (!r.Bytes(8).SequenceEqual(PngSignature))
            throw new InvalidDataException("Page is not a PNG.");
        while (true)
        {
            var header = r.Bytes(8);
            int chunkLength = BinaryPrimitives.ReadInt32BigEndian(header); // PNG is always big-endian
            if (chunkLength < 0)
                throw new InvalidDataException("Bad PNG chunk.");
            long next = r.Position + (long)chunkLength + 4; // data + CRC (long: a huge length must not wrap)
            if (next > r.Length)
                throw new InvalidDataException("Bad PNG chunk.");
            r.Position = next;
            if (header.AsSpan(4, 4).SequenceEqual("IEND"u8))
                return r.Position - start;
        }
    }

    public static byte[] ReadPng(Stream stream, PackPage page)
    {
        stream.Position = page.PngOffset;
        var png = new byte[page.PngLength];
        stream.ReadExactly(png);
        return png;
    }

    sealed class Reader(Stream stream, bool little)
    {
        public long Position { get => stream.Position; set => stream.Position = value; }
        public long Length => stream.Length;

        public byte[] Bytes(int n)
        {
            if (stream.Position + n > stream.Length)
                throw new InvalidDataException("Unexpected end of file.");
            var buffer = new byte[n];
            stream.ReadExactly(buffer);
            return buffer;
        }

        public int Int()
        {
            var b = Bytes(4);
            return little ? BinaryPrimitives.ReadInt32LittleEndian(b) : BinaryPrimitives.ReadInt32BigEndian(b);
        }

        public int Count()
        {
            int n = Int();
            if (n is < 0 or > MaxCount)
                throw new InvalidDataException("Bad count.");
            return n;
        }

        public string String()
        {
            int n = Int();
            if (n is < 0 or > 4096)
                throw new InvalidDataException("Bad string.");
            return Encoding.UTF8.GetString(Bytes(n));
        }
    }
}

using System.Text;
using SpiffoCON.Core.Catalog;

namespace SpiffoCON.Core.Tests;

public class TexturePackTests
{
    // a 1x1 PNG
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>Writes a pack the way the game's files are laid out.</summary>
    static byte[] Pack(int version, bool bigEndian = false)
    {
        var ms = new MemoryStream();
        void Int(int v)
        {
            var b = BitConverter.GetBytes(v);
            if (bigEndian) Array.Reverse(b);
            ms.Write(b);
        }
        void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            Int(b.Length);
            ms.Write(b);
        }
        if (version >= 1)
        {
            ms.Write("PZPK"u8);
            Int(version);
        }
        Int(2); // pages
        foreach (var (page, entries) in new[] { ("UI2_0", new[] { "Item_Axe", "Item_Hammer" }), ("UI2_1", new[] { "Item_Katana" }) })
        {
            Str(page);
            Int(entries.Length);
            Int(1); // alpha
            int x = 0;
            foreach (var e in entries)
            {
                Str(e);
                foreach (var v in new[] { x, 0, 30, 28, 1, 2, 32, 32 })
                    Int(v);
                x += 32;
            }
            if (version >= 1)
                Int(Png.Length);
            ms.Write(Png);
            if (version == 0)
                ms.Write(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE });
        }
        return ms.ToArray();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void Reads_pages_entries_and_pngs(int version, bool bigEndian)
    {
        using var stream = new MemoryStream(Pack(version, bigEndian));
        var pages = TexturePack.ReadIndex(stream);

        Assert.Equal(["UI2_0", "UI2_1"], pages.Select(p => p.Name));
        Assert.Equal(["Item_Axe", "Item_Hammer"], pages[0].Entries.Select(e => e.Name));
        var hammer = pages[0].Entries[1];
        Assert.Equal((32, 0, 30, 28, 1, 2, 32, 32), (hammer.X, hammer.Y, hammer.Width, hammer.Height, hammer.OffsetX, hammer.OffsetY, hammer.FullWidth, hammer.FullHeight));
        Assert.True(pages[1].HasAlpha);
        foreach (var page in pages)
            Assert.Equal(Png, TexturePack.ReadPng(stream, page));
    }

    [Fact]
    public void Rejects_other_files()
    {
        using var stream = new MemoryStream(Png);
        Assert.Throws<InvalidDataException>(() => TexturePack.ReadIndex(stream));
    }
}

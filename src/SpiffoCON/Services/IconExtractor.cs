using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpiffoCON.Core.Catalog;
using SpiffoCON.Core.Steam;

namespace SpiffoCON.Services;

/// <summary>
/// Takes the base-game item icons (Item_*.png) out of the user's own Project Zomboid install
/// (media/texturepacks/*.pack) into SpiffoCON's cache. The dedicated server has no textures, and
/// the game's art isn't SpiffoCON's to ship, so this is the only source.
/// </summary>
public static class IconExtractor
{
    /// <summary>media/texturepacks of a game install found in the Steam libraries, if any.</summary>
    public static string? FindTexturePacks()
    {
        foreach (var library in SteamLocator.FindLibraries())
        {
            var packs = Path.Combine(library, "steamapps", "common", "ProjectZomboid", "media", "texturepacks");
            if (Directory.Exists(packs) && Directory.EnumerateFiles(packs, "*.pack").Any())
                return packs;
        }
        return null;
    }

    /// <summary>Accepts the game folder, its media folder or the texturepacks folder itself.</summary>
    public static string? ResolveTexturePacks(string folder)
    {
        foreach (var candidate in new[] { folder, Path.Combine(folder, "texturepacks"), Path.Combine(folder, "media", "texturepacks") })
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.pack").Any())
                return candidate;
        return null;
    }

    /// <summary>Writes one PNG per Item_ sprite into <paramref name="target"/>; returns how many.</summary>
    public static int Extract(string texturePacks, string target, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(target);
        int written = 0;
        var packs = Directory.GetFiles(texturePacks, "*.pack");
        for (int n = 0; n < packs.Length; n++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Reading {Path.GetFileName(packs[n])} ({n + 1}/{packs.Length})...");
            using var stream = File.OpenRead(packs[n]);
            IReadOnlyList<PackPage> pages;
            try
            {
                pages = TexturePack.ReadIndex(stream);
            }
            catch (InvalidDataException)
            {
                continue; // not a pack SpiffoCON understands: skip it
            }
            foreach (var page in pages)
            {
                var items = page.Entries.Where(e => e.Name.StartsWith("Item_", StringComparison.OrdinalIgnoreCase)).ToList();
                if (items.Count == 0)
                    continue;
                written += ExtractPage(TexturePack.ReadPng(stream, page), items, target);
            }
        }
        return written;
    }

    static int ExtractPage(byte[] png, IReadOnlyList<PackEntry> entries, string target)
    {
        BitmapSource atlas;
        using (var ms = new MemoryStream(png))
        {
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            atlas = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        }
        int atlasWidth = atlas.PixelWidth, atlasHeight = atlas.PixelHeight;
        var pixels = new byte[atlasWidth * atlasHeight * 4];
        atlas.CopyPixels(pixels, atlasWidth * 4, 0);

        int written = 0;
        foreach (var e in entries)
        {
            // the sprite was trimmed: put it back at its offset in the full-size canvas
            int width = Math.Max(e.FullWidth, e.OffsetX + e.Width);
            int height = Math.Max(e.FullHeight, e.OffsetY + e.Height);
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024)
                continue;
            var canvas = new byte[width * height * 4];
            for (int row = 0; row < e.Height; row++)
            {
                int sy = e.Y + row, dy = e.OffsetY + row;
                if (sy < 0 || sy >= atlasHeight || dy < 0 || dy >= height)
                    continue;
                int copy = Math.Min(e.Width, Math.Min(atlasWidth - e.X, width - e.OffsetX));
                if (copy <= 0 || e.X < 0 || e.OffsetX < 0)
                    continue;
                Buffer.BlockCopy(pixels, (sy * atlasWidth + e.X) * 4, canvas, (dy * width + e.OffsetX) * 4, copy * 4);
            }
            var icon = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(icon));
            var name = string.Concat(e.Name.Split(Path.GetInvalidFileNameChars()));
            using var file = File.Create(Path.Combine(target, name + ".png"));
            encoder.Save(file);
            written++;
        }
        return written;
    }
}

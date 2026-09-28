// Draws SpiffoCON's application icon: src/SpiffoCON/Assets/spiffocon.ico (16-256 px, PNG entries).
//   dotnet run tools/MakeAppIcon.cs
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var target = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "..", "src", "SpiffoCON", "Assets", "spiffocon.ico"));
var t = new Thread(() =>
{
    int[] sizes = [16, 24, 32, 48, 64, 128, 256];
    var pngs = sizes.Select(s => Png(Draw(s))).ToList();
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    using var file = File.Create(target);
    using var w = new BinaryWriter(file);
    // ICONDIR + ICONDIRENTRY[] + PNG data (Vista+ icons may hold PNG images)
    w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32);
        w.Write(pngs[i].Length); w.Write(offset);
        offset += pngs[i].Length;
    }
    foreach (var png in pngs)
        w.Write(png);
});
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
Console.WriteLine("icon written to " + target);

// a dark tile with an orange console prompt ">_"
static BitmapSource Draw(int size)
{
    double s = size / 256.0;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        var bg = new LinearGradientBrush(Color.FromRgb(0x2A, 0x33, 0x3E), Color.FromRgb(0x12, 0x17, 0x1D), 90);
        double pad = size <= 24 ? 0 : 8 * s;
        dc.DrawRoundedRectangle(bg, new Pen(new SolidColorBrush(Color.FromRgb(0xD2, 0x8A, 0x1E)), Math.Max(1, 8 * s)),
            new Rect(pad + 4 * s, pad + 4 * s, size - 2 * pad - 8 * s, size - 2 * pad - 8 * s), 40 * s, 40 * s);
        var accent = new SolidColorBrush(Color.FromRgb(0xF2, 0xA0, 0x2E));
        var pen = new Pen(accent, Math.Max(1.5, 26 * s)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var chevron = new StreamGeometry();
        using (var g = chevron.Open())
        {
            g.BeginFigure(new Point(70 * s, 80 * s), false, false);
            g.LineTo(new Point(126 * s, 128 * s), true, true);
            g.LineTo(new Point(70 * s, 176 * s), true, true);
        }
        dc.DrawGeometry(null, pen, chevron);
        dc.DrawLine(new Pen(Brushes.White, Math.Max(1.5, 24 * s)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            new Point(146 * s, 180 * s), new Point(196 * s, 180 * s));
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(dv);
    return bmp;
}

static byte[] Png(BitmapSource bmp)
{
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(bmp));
    using var ms = new MemoryStream();
    enc.Save(ms);
    return ms.ToArray();
}

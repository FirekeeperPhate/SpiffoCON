// Draws the SpiffoCON Bridge images: preview.png (Workshop, 256x256) and poster.png (mod list).
//   dotnet run tools/MakeBridgeImages.cs
#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", "..", "bridge", "SpiffoCONBridge"));
var t = new Thread(() =>
{
    Save(Draw(256), Path.Combine(root, "preview.png"));
    Save(Draw(512), Path.Combine(root, "Contents", "mods", "SpiffoCONBridge", "42", "poster.png"));
});
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
Console.WriteLine("images written under " + root);

static RenderTargetBitmap Draw(int size)
{
    double s = size / 256.0;
    var dv = new DrawingVisual();
    using (var dc = dv.RenderOpen())
    {
        var bg = new LinearGradientBrush(Color.FromRgb(0x1B, 0x22, 0x2B), Color.FromRgb(0x0E, 0x12, 0x17), 90);
        dc.DrawRectangle(bg, null, new Rect(0, 0, size, size));
        // two "terminals" joined by a line: SpiffoCON <-> server
        var accent = new SolidColorBrush(Color.FromRgb(0xD2, 0x8A, 0x1E));
        var pen = new Pen(accent, 6 * s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawRoundedRectangle(null, pen, new Rect(28 * s, 58 * s, 70 * s, 54 * s), 8 * s, 8 * s);
        dc.DrawRoundedRectangle(null, pen, new Rect(158 * s, 58 * s, 70 * s, 54 * s), 8 * s, 8 * s);
        dc.DrawLine(new Pen(Brushes.White, 5 * s) { DashStyle = new DashStyle([1.5, 1.5], 0) }, new Point(104 * s, 85 * s), new Point(152 * s, 85 * s));
        Text(dc, "SpiffoCON", 34 * s, Brushes.White, size, 140 * s, FontWeights.Bold);
        Text(dc, "BRIDGE", 26 * s, accent, size, 182 * s, FontWeights.SemiBold);
        Text(dc, "server side · B42", 13 * s, new SolidColorBrush(Color.FromRgb(0x9A, 0xA7, 0xB4)), size, 222 * s, FontWeights.Normal);
    }
    var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bmp.Render(dv);
    return bmp;
}

static void Text(DrawingContext dc, string text, double em, Brush brush, int size, double y, FontWeight weight)
{
    var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal), em, brush, 1.0);
    dc.DrawText(ft, new Point((size - ft.Width) / 2, y));
}

static void Save(BitmapSource bmp, string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var png = new PngBitmapEncoder();
    png.Frames.Add(BitmapFrame.Create(bmp));
    using var f = File.Create(path);
    png.Save(f);
}

using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace SpiffoCON.Controls;

/// <summary>Icon file path → small frozen bitmap, cached (the list reuses rows while scrolling).</summary>
public sealed class IconConverter : IValueConverter
{
    static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path)
            return null;
        return Cache.GetOrAdd(path, Load);
    }

    static BitmapImage? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 32;
            image.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file open
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or ArgumentException)
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

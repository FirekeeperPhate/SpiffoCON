using System.Globalization;

namespace SpiffoCON.Core.Commands;

/// <summary>A PZ rich-text color: each channel 0..1.</summary>
public readonly record struct RgbColor(float R, float G, float B)
{
    public static readonly RgbColor White = new(1f, 1f, 1f);

    /// <summary>The chat tag, always with '.' decimals (an Italian locale would otherwise write "0,5").</summary>
    public string ToTag() => $"<RGB:{Format(R)},{Format(G)},{Format(B)}>";

    public static RgbColor FromBytes(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f);

    public (byte R, byte G, byte B) ToBytes() => (ToByte(R), ToByte(G), ToByte(B));

    static string Format(float value) =>
        Math.Round(Math.Clamp(value, 0f, 1f), 2).ToString("0.##", CultureInfo.InvariantCulture);

    static byte ToByte(float value) => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
}

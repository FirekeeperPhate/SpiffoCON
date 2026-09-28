using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SpiffoCON.Core.Commands;

namespace SpiffoCON.Controls;

/// <summary>Fills a TextBlock with colored runs from a message preview line.</summary>
public static class RichText
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<MessageSegment>), typeof(RichText),
        new PropertyMetadata(null, OnSegmentsChanged));

    public static IReadOnlyList<MessageSegment>? GetSegments(DependencyObject d) =>
        (IReadOnlyList<MessageSegment>?)d.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject d, IReadOnlyList<MessageSegment>? value) =>
        d.SetValue(SegmentsProperty, value);

    static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;
        block.Inlines.Clear();
        var segments = e.NewValue as IReadOnlyList<MessageSegment>;
        if (segments is null || segments.Count == 0)
        {
            block.Inlines.Add(new Run(" ")); // keeps the height of an empty line
            return;
        }
        foreach (var s in segments)
        {
            var (r, g, b) = s.Color.ToBytes();
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            block.Inlines.Add(new Run(s.Text) { Foreground = brush });
        }
    }
}

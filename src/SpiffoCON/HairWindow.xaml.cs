using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SpiffoCON.Core.Bridge;
using SpiffoCON.ViewModels;

namespace SpiffoCON;

/// <summary>
/// Picks a hair style for a player, from the styles the bridge lists for their gender, and with bridge v10
/// a beard (men) and the colour of hair and beard.
/// </summary>
public partial class HairWindow : Window
{
    sealed record Row(string Name, string Text);

    readonly BridgeHairStyles _styles;
    readonly List<Row> _rows;
    readonly List<ToggleButton> _swatches = [];
    BridgeColor? _color;

    /// <summary>What was picked (null fields: unchanged), or null when cancelled.</summary>
    public HairChoice? Chosen { get; private set; }

    public HairWindow(string player, BridgeHairStyles styles)
    {
        InitializeComponent();
        _styles = styles;
        TitleText.Text = $"Hair and beard for {player}";
        CurrentText.Text = $"Now: {Describe(styles.Current, styles)}"
            + (styles.Beards is not null ? $", {DescribeBeard(styles.Beard, styles)}" : "")
            + ". The new look is saved with the character and shown to the players near them; hair can be cut and grows as usual.";
        // shortest first, as the game's own hair menu
        _rows = styles.Styles
            .OrderBy(s => s.Level ?? int.MaxValue)
            .ThenBy(s => s.Label ?? s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new Row(s.Name, Text(s)))
            .ToList();
        StyleList.ItemsSource = _rows;
        // the style they have is picked: only what the user changes is sent
        StyleList.SelectedItem = _rows.FirstOrDefault(r => r.Name == styles.Current);

        if (styles.Beards is { } beards)
        {
            var beardRows = new List<Row> { new("", "None") };
            beardRows.AddRange(beards.OrderBy(b => b.Level ?? int.MaxValue).ThenBy(b => b.Label ?? b.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(b => new Row(b.Name, Text(b))));
            BeardBox.ItemsSource = beardRows;
            BeardBox.SelectedItem = beardRows.FirstOrDefault(r => r.Name == (styles.Beard ?? ""));
            BeardPanel.Visibility = Visibility.Visible;
        }
        if (styles.Colors.Count > 0)
        {
            foreach (var c in styles.Colors)
            {
                var swatch = new ToggleButton
                {
                    Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(3), Tag = c,
                    Content = new Border { Background = new SolidColorBrush(ToColor(c)), CornerRadius = new CornerRadius(3), Width = 20, Height = 20 },
                    ToolTip = $"{(int)(c.R * 255)}, {(int)(c.G * 255)}, {(int)(c.B * 255)}",
                };
                swatch.Click += Swatch_Click;
                _swatches.Add(swatch);
                ColorSwatches.Children.Add(swatch);
            }
            ColorPanel.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) =>
        {
            StyleList.ScrollIntoView(StyleList.SelectedItem);
            FilterBox.Focus();
        };
    }

    static Color ToColor(BridgeColor c) => Color.FromRgb((byte)Math.Round(c.R * 255), (byte)Math.Round(c.G * 255), (byte)Math.Round(c.B * 255));

    internal static string Text(BridgeHairStyle s) =>
        (s.Label is { Length: > 0 } label && label != s.Name ? $"{label} ({s.Name})" : s.Name)
        + (s.Level is { } level ? $" · length {level}" : "");

    internal static string Describe(string? current, BridgeHairStyles styles) =>
        current is null or "" || current.Equals("Bald", StringComparison.OrdinalIgnoreCase) ? "bald"
        : styles.Styles.FirstOrDefault(s => s.Name == current) is { } style ? Text(style)
        : current;

    internal static string DescribeBeard(string? beard, BridgeHairStyles styles) =>
        beard is null or "" ? "no beard"
        : "beard " + (styles.Beards?.FirstOrDefault(b => b.Name == beard) is { } style ? Text(style) : beard);

    HairChoice Choice() => new(
        StyleList.SelectedItem is Row hair && hair.Name != _styles.Current ? hair.Name : null,
        BeardBox.SelectedItem is Row beard && beard.Name != (_styles.Beard ?? "") ? beard.Name : null,
        _color);

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();
        var selected = StyleList.SelectedItem;
        StyleList.ItemsSource = filter.Length == 0 ? _rows : _rows.Where(r => r.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (selected is not null && StyleList.Items.Contains(selected))
            StyleList.SelectedItem = selected;
    }

    void Choice_Changed(object sender, SelectionChangedEventArgs e) => SetButton.IsEnabled = !Choice().IsEmpty;

    void Swatch_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        foreach (var s in _swatches)
            s.IsChecked = ReferenceEquals(s, clicked) && clicked.IsChecked == true;
        _color = clicked.IsChecked == true ? (BridgeColor)clicked.Tag : null;
        SetButton.IsEnabled = !Choice().IsEmpty;
    }

    void StyleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!Choice().IsEmpty)
            Set_Click(sender, e);
    }

    void Set_Click(object sender, RoutedEventArgs e)
    {
        var choice = Choice();
        if (choice.IsEmpty)
            return;
        Chosen = choice;
        DialogResult = true;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // arrows from the filter box move in the list
        if (e.Key is Key.Down or Key.Up && FilterBox.IsKeyboardFocused && StyleList.Items.Count > 0)
        {
            StyleList.SelectedIndex = Math.Clamp(StyleList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, StyleList.Items.Count - 1);
            StyleList.ScrollIntoView(StyleList.SelectedItem);
            e.Handled = true;
        }
    }
}

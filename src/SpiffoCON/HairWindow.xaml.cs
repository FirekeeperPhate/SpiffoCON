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
    sealed record Row(string Name, string Text, string Search);

    readonly BridgeHairStyles _styles;
    readonly List<Row> _rows;
    readonly List<ToggleButton> _swatches = [];
    BridgeColor? _color;

    // the hair style the user picked: kept across filters, which empty the list's selection when they hide it
    Row? _picked;
    bool _refiltering;

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
            .Select(s => new Row(s.Name, Text(s), s.Label is { Length: > 0 } label ? $"{label} {s.Name}" : s.Name))
            .ToList();
        StyleList.ItemsSource = _rows;
        // the style they have is picked: only what the user changes is sent
        _picked = _rows.FirstOrDefault(r => r.Name == styles.Current);
        StyleList.SelectedItem = _picked;

        // an empty list (the bridge could not read the beards) offers nothing: "None" alone would shave
        if (styles.Beards is { Count: > 0 } beards)
        {
            var beardRows = new List<Row> { new("", "None", "") };
            beardRows.AddRange(beards.OrderBy(b => b.Level ?? int.MaxValue).ThenBy(b => b.Label ?? b.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(b => new Row(b.Name, Text(b), b.Name)));
            BeardBox.ItemsSource = beardRows;
            BeardBox.SelectedItem = beardRows.FirstOrDefault(r => r.Name == (styles.Beard ?? ""));
            BeardPanel.Visibility = Visibility.Visible;
        }
        if (styles.Colors is { Count: > 0 } offered)
        {
            // the colour they have comes first and is picked (a dyed one is not among the game's own)
            var colors = offered.ToList();
            int current = styles.HairColor is { } now ? colors.FindIndex(c => Same(c, now)) : -1;
            if (styles.HairColor is { } dyed && current < 0)
            {
                colors.Insert(0, dyed);
                current = 0;
            }
            for (int i = 0; i < colors.Count; i++)
            {
                var c = colors[i];
                var swatch = new ToggleButton
                {
                    Width = 30, Height = 30, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(3), Tag = c,
                    Content = new Border { Background = new SolidColorBrush(ToColor(c)), CornerRadius = new CornerRadius(3), Width = 20, Height = 20 },
                    ToolTip = $"{Channel(c.R)}, {Channel(c.G)}, {Channel(c.B)}" + (i == current ? " (now)" : ""),
                    IsChecked = i == current,
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

    static byte Channel(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    static Color ToColor(BridgeColor c) => Color.FromRgb(Channel(c.R), Channel(c.G), Channel(c.B));

    // the same 8-bit colour: the game keeps colours as floats
    static bool Same(BridgeColor a, BridgeColor b) => Channel(a.R) == Channel(b.R) && Channel(a.G) == Channel(b.G) && Channel(a.B) == Channel(b.B);

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
        _picked is { } hair && hair.Name != _styles.Current ? hair.Name : null,
        BeardBox.SelectedItem is Row beard && beard.Name != (_styles.Beard ?? "") ? beard.Name : null,
        _color);

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();
        _refiltering = true;
        try
        {
            // the names only: the text also says "length N"
            StyleList.ItemsSource = filter.Length == 0 ? _rows : _rows.Where(r => r.Search.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (_picked is not null && StyleList.Items.Contains(_picked))
                StyleList.SelectedItem = _picked;
        }
        finally
        {
            _refiltering = false;
        }
    }

    void StyleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_refiltering)
            _picked = StyleList.SelectedItem as Row;
        SetButton.IsEnabled = !Choice().IsEmpty;
    }

    void Choice_Changed(object sender, SelectionChangedEventArgs e) => SetButton.IsEnabled = !Choice().IsEmpty;

    void Swatch_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        foreach (var s in _swatches)
            s.IsChecked = ReferenceEquals(s, clicked) && clicked.IsChecked == true;
        var color = clicked.IsChecked == true ? (BridgeColor)clicked.Tag : null;
        // the colour they have already is no change
        _color = color is not null && _styles.HairColor is { } now && Same(color, now) ? null : color;
        SetButton.IsEnabled = !Choice().IsEmpty;
    }

    void StyleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // a double click on a row, not on the scroll bar or the empty space under the rows
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(StyleList, source) is ListBoxItem
            && !Choice().IsEmpty)
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

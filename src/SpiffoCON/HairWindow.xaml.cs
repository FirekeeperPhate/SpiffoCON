using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SpiffoCON.Core.Bridge;

namespace SpiffoCON;

/// <summary>Picks a hair style for a player, from the styles the bridge lists for their gender.</summary>
public partial class HairWindow : Window
{
    sealed record Row(string Name, string Text);

    readonly List<Row> _rows;

    /// <summary>The style picked (the game's id), or null when cancelled.</summary>
    public string? Chosen { get; private set; }

    public HairWindow(string player, BridgeHairStyles styles)
    {
        InitializeComponent();
        TitleText.Text = $"Hair style for {player}";
        CurrentText.Text = $"Now: {Describe(styles.Current, styles)}. The new style is saved with the character and shown to "
            + "the players near them; it can be cut and grows as usual.";
        // shortest first, as the game's own hair menu
        _rows = styles.Styles
            .OrderBy(s => s.Level ?? int.MaxValue)
            .ThenBy(s => s.Label ?? s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new Row(s.Name, Text(s)))
            .ToList();
        StyleList.ItemsSource = _rows;
        Loaded += (_, _) => FilterBox.Focus();
    }

    internal static string Text(BridgeHairStyle s) =>
        (s.Label is { Length: > 0 } label && label != s.Name ? $"{label} ({s.Name})" : s.Name)
        + (s.Level is { } level ? $" · length {level}" : "");

    internal static string Describe(string? current, BridgeHairStyles styles) =>
        current is null or "" || current.Equals("Bald", StringComparison.OrdinalIgnoreCase) ? "bald"
        : styles.Styles.FirstOrDefault(s => s.Name == current) is { } style ? Text(style)
        : current;

    void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();
        StyleList.ItemsSource = filter.Length == 0 ? _rows : _rows.Where(r => r.Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    void StyleList_SelectionChanged(object sender, SelectionChangedEventArgs e) => SetButton.IsEnabled = StyleList.SelectedItem is Row;

    void StyleList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StyleList.SelectedItem is Row)
            Set_Click(sender, e);
    }

    void Set_Click(object sender, RoutedEventArgs e)
    {
        if (StyleList.SelectedItem is not Row row)
            return;
        Chosen = row.Name;
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

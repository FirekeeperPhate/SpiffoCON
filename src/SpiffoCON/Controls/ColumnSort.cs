using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SpiffoCON.Core.Bridge;

namespace SpiffoCON.Controls;

/// <summary>
/// <c>Enabled="True"</c> on a ListView: a click on a column's header sorts the rows by what <c>By</c> says on
/// that column (several, comma separated: the next ones for ties), a second click the other way. The order
/// stays while the list is read again.
/// </summary>
public static class ColumnSort
{
    const string Up = " ▲", Down = " ▼";

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ColumnSort), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty ByProperty = DependencyProperty.RegisterAttached(
        "By", typeof(string), typeof(ColumnSort), new PropertyMetadata(null));

    public static string? GetBy(DependencyObject d) => (string?)d.GetValue(ByProperty);

    public static void SetBy(DependencyObject d, string? value) => d.SetValue(ByProperty, value);

    // the column a list is sorted by
    static readonly DependencyProperty CurrentProperty = DependencyProperty.RegisterAttached(
        "Current", typeof(GridViewColumn), typeof(ColumnSort), new PropertyMetadata(null));

    static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListView list)
            return;
        list.RemoveHandler(ButtonBase.ClickEvent, (RoutedEventHandler)Clicked);
        if ((bool)e.NewValue)
            list.AddHandler(ButtonBase.ClickEvent, (RoutedEventHandler)Clicked);
    }

    static void Clicked(object sender, RoutedEventArgs e)
    {
        // (the buttons in the rows click too, and a header past the last column has no column)
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column } || GetBy(column) is not { Length: > 0 } by)
            return;
        var list = (ListView)sender;
        if (CollectionViewSource.GetDefaultView(list.ItemsSource) is not ListCollectionView view)
            return;

        var current = (GridViewColumn?)list.GetValue(CurrentProperty);
        bool descending = current == column && column.Header is string shown && shown.EndsWith(Up);
        if (current?.Header is string old)
            current.Header = Title(old);
        column.Header = Title((string)column.Header) + (descending ? Down : Up);
        list.SetValue(CurrentProperty, column);
        view.CustomSort = new RowOrder(by.Split(','), descending);
    }

    static string Title(string header) => header.EndsWith(Up) || header.EndsWith(Down) ? header[..^Up.Length] : header;
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using SpiffoCON.ViewModels;

namespace SpiffoCON;

/// <summary>
/// One vehicle in a window of its own ("Vehicle details"), as the game's mechanics window: a plan of the
/// vehicle with each part in the colour of its condition, the parts by category with their percentage, the
/// ones that are gone, and a repair for each.
/// </summary>
public partial class VehicleWindow : Window
{
    readonly VehicleViewModel _vm;

    public VehicleWindow(VehicleViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // under the game's own categories ("Tires", "Brakes"...)
        CollectionViewSource.GetDefaultView(vm.Parts).GroupDescriptions.Add(new PropertyGroupDescription(nameof(VehiclePartRow.Category)));
        vm.Removed += (_, _) => Close();
        Closed += (_, _) => vm.Dispose();
    }

    public int VehicleId => _vm.Id;

    // a part of the drawing: chosen in the list too
    void Shape_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: VehicleShape shape })
            _vm.Select(shape.PartId);
    }

    void PartsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartsList.SelectedItem is { } chosen)
            PartsList.ScrollIntoView(chosen);
    }

    // who gets the key: the players online now
    void GiveKey_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (Button)sender, Placement = PlacementMode.Left };
        var players = _vm.KeyTakers;
        if (players.Count == 0)
            menu.Items.Add(new MenuItem { Header = "Nobody is online", IsEnabled = false });
        foreach (var player in players)
        {
            var item = new MenuItem { Header = player };
            item.Click += async (_, _) => await _vm.GiveKeyAsync(player);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}

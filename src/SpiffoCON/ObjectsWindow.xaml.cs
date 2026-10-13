using System.Windows;
using SpiffoCON.ViewModels;

namespace SpiffoCON;

/// <summary>The objects of the world around a player, in a window of their own: listed, and removed one by one.</summary>
public partial class ObjectsWindow : Window
{
    public ObjectsWindow(ObjectsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
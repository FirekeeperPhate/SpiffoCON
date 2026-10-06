using System.Windows;
using SpiffoCON.Controls;
using SpiffoCON.ViewModels;

namespace SpiffoCON;

/// <summary>
/// Everything about one player in a window of its own ("Character details" in the player menu): who and
/// where, health, traits, skills, equipment, inventory, and the same actions as the right-click menu.
/// </summary>
public partial class CharacterWindow : Window
{
    readonly CharacterViewModel _vm;

    public CharacterWindow(CharacterViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Updated += (_, _) => FillActions();
        FillActions();
        Closed += (_, _) => vm.Dispose();
    }

    public string Username => _vm.Username;

    // what can be done changes with who is online, the bridge and its version: built again at each read,
    // unless a submenu is open (it would close under the pointer)
    void FillActions()
    {
        if (ActionsMenu.IsKeyboardFocusWithin || ActionsMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(m => m.IsSubmenuOpen))
            return;
        PlayerMenu.Fill(ActionsMenu.Items, _vm.Actions, _vm.Username, inWindow: true);
        // a bar that wraps has no use for the menu's dividing lines
        foreach (var line in ActionsMenu.Items.OfType<System.Windows.Controls.Separator>().ToList())
            ActionsMenu.Items.Remove(line);
        // in a bar, what opens a list of choices says so
        foreach (var item in ActionsMenu.Items.OfType<System.Windows.Controls.MenuItem>())
            if (item.Items.Count > 0 && item.Header is string header)
                item.Header = header + "  \x25BE";
    }
}

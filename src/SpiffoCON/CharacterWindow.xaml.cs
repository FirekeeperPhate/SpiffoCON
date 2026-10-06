using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    // the menu of choices open from an action or a skill's "+": while it shows, the actions are not rebuilt
    ContextMenu? _open;

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

    // what can be done changes with who is online, the bridge and its version: built again at each read. The
    // items are the player menu's own (one place says what there is and when it is possible), shown as a
    // column of buttons: one with choices opens them as a menu beside it.
    void FillActions()
    {
        if (_open is { IsOpen: true })
            return;
        var source = new ContextMenu();
        PlayerMenu.Fill(source.Items, _vm.Actions, _vm.Username, inWindow: true);
        ActionsPanel.Children.Clear();
        double gap = 0;
        foreach (var entry in source.Items.Cast<object>().ToList())
        {
            if (entry is Separator)
            {
                // a little air where the menu has a line
                gap = 6;
                continue;
            }
            if (entry is not MenuItem item)
                continue;
            var choices = item.Items.Cast<object>().ToList();
            var button = new Button
            {
                Content = item.Header is string header && choices.Count > 0 ? header + "  \x25B8" : item.Header,
                IsEnabled = item.IsEnabled,
                ToolTip = item.ToolTip,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, gap, 0, 3),
            };
            gap = 0;
            ToolTipService.SetShowOnDisabled(button, true);
            if (choices.Count > 0)
            {
                // the choices move from the menu item to a menu of their own, opened to the left of the column
                item.Items.Clear();
                var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Left };
                foreach (var choice in choices)
                    menu.Items.Add(choice);
                button.Click += (_, _) => Open(menu);
            }
            else
            {
                button.Click += (_, _) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            ActionsPanel.Children.Add(button);
        }
    }

    void Open(ContextMenu menu)
    {
        _open = menu;
        menu.IsOpen = true;
    }

    // English text whatever the PC's culture: "2,500"
    static string Number(int value) => value.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture);

    // "+" on a skill: how much experience
    void SkillPlus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CharacterSkill skill } button || !skill.CanAdd)
            return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        void Add(string header, int amount, string? tip = null)
        {
            var item = new MenuItem { Header = header, ToolTip = tip };
            item.Click += async (_, _) => await _vm.AddXpAsync(skill, amount);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuItem { Header = $"Experience for {skill.Name}", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        if (skill.ToNextLevel is { } toNext)
            Add($"To level {skill.Level + 1} (+{Number(toNext)} XP)", toNext, "What is missing to the next level");
        foreach (var amount in new[] { 25, 100, 500, 2500 })
            Add($"+{Number(amount)} XP", amount);
        Open(menu);
    }
}

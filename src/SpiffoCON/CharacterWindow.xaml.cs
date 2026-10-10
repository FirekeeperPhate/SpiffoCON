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

    // right-click on a row of the inventory: what can be done to those items
    void Inventory_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(InventoryList, source) is ListViewItem { DataContext: CharacterItem item } row)
        {
            InventoryList.SelectedItem = item;
            OpenItemMenu(item, row);
            e.Handled = true;
        }
    }

    // on something held, worn or attached: the same menu, for the row of the inventory it is in
    void Equipment_MouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CharacterEquipment equipment } row && _vm.RowOf(equipment) is { } item)
        {
            OpenItemMenu(item, row);
            e.Handled = true;
        }
    }

    void OpenItemMenu(CharacterItem item, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        string title = (item.Count == 1 ? item.Name : $"{item.Name} × {item.Count}")
            + (item.Container is "Inventory" or "" ? "" : $"  ({item.ContainerShort})");
        menu.Items.Add(new MenuItem { Header = title, IsEnabled = false, FontWeight = FontWeights.SemiBold });

        void Add(string header, Func<Task> action, bool enabled = true, string? tip = null)
        {
            var entry = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
            ToolTipService.SetShowOnDisabled(entry, true);
            entry.Click += async (_, _) => await action();
            menu.Items.Add(entry);
        }
        // through the bridge (v14): each is there when the row has something to do it to
        var problem = _vm.ItemActionsProblem;
        void Bridge(string header, Core.Bridge.BridgeClient.ItemAction action, bool applies, string what)
        {
            if (problem is null && !applies)
                return;
            Add(header, () => _vm.ItemActionAsync(item, action), problem is null, problem ?? what);
        }
        string all = item.Count > 1 ? $" (all {item.Count})" : "";
        Bridge("Repair" + all, Core.Bridge.BridgeClient.ItemAction.Repair, item.Item.Condition is not null,
            "As new: the condition, the head of a tool, the edge of a blade; clothes are mended, their patches taken off, and washed");
        Bridge("Clean" + all, Core.Bridge.BridgeClient.ItemAction.Clean, item.Item.Washable == true,
            "Blood and dirt off, and dry");
        Bridge("Fill with water" + all, Core.Bridge.BridgeClient.ItemAction.Fill, item.Item is { Fill: < 1, Water: not false },
            "Water to the top, on what is in it already (a container that takes no water is left)");
        Bridge("Empty…" + all, Core.Bridge.BridgeClient.ItemAction.Empty, item.Item.Fill is > 0,
            "The liquid inside is thrown away (asked first)");
        Bridge("Recharge" + all, Core.Bridge.BridgeClient.ItemAction.Recharge, item.Item.Uses is not null,
            "Full again: a battery, a lighter, a spool of thread");
        if (menu.Items.Count > 1)
            menu.Items.Add(new Separator());

        Add("Give one more", () => _vm.GiveOneMoreAsync(item), _vm.CanGive,
            _vm.CanGive ? "A new one into their main inventory (RCON additem)" : "Needs the player online (connect RCON)");
        Add("Remove one…", () => _vm.RemoveAsync(item, all: false));
        if (item.Count > 1)
            Add($"Remove all {item.Count}…", () => _vm.RemoveAsync(item, all: true));
        menu.Items.Add(new Separator());
        Add("Show in the Catalog", () => { _vm.ShowInCatalog(item); return Task.CompletedTask; });
        Add("Copy the id", () => { _vm.CopyId(item); return Task.CompletedTask; }, tip: item.FullType);
        Open(menu);
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
        // straight to a level, up or down (bridge v16 says what each level takes)
        menu.Items.Add(new Separator());
        bool can = _vm.CanSetLevel(skill);
        var levels = new MenuItem
        {
            Header = "Set level",
            IsEnabled = can,
            ToolTip = can ? "Adds, or takes away, the experience between now and the start of that level"
                : "Setting a level needs bridge v16, which says what each level takes",
        };
        ToolTipService.SetShowOnDisabled(levels, true);
        for (int level = 0; level <= 10; level++)
        {
            int target = level;
            var entry = new MenuItem { Header = level.ToString(System.Globalization.CultureInfo.InvariantCulture), IsChecked = level == skill.Level };
            entry.Click += async (_, _) => await _vm.SetLevelAsync(skill, target);
            levels.Items.Add(entry);
        }
        menu.Items.Add(levels);
        Open(menu);
    }
}

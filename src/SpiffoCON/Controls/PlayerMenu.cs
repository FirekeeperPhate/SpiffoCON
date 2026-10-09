using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SpiffoCON.Core.Bridge;
using SpiffoCON.Core.Files;
using SpiffoCON.ViewModels;

namespace SpiffoCON.Controls;

/// <summary>
/// The right-click menu on a player, the same in every list that shows players:
/// <c>controls:PlayerMenu.Enabled="True"</c> on the list. Items can be player names, bridge players,
/// accounts or chat lines. Built when it opens, so it follows who is online, the kits and the bridge.
/// </summary>
public static class PlayerMenu
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PlayerMenu), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
            return;
        list.ContextMenuOpening -= OnOpening;
        if ((bool)e.NewValue)
        {
            // an (empty) menu makes WPF raise ContextMenuOpening; it is filled there
            list.ContextMenu ??= new ContextMenu();
            list.ContextMenuOpening += OnOpening;
        }
    }

    static void OnOpening(object sender, ContextMenuEventArgs e)
    {
        var list = (ItemsControl)sender;
        var container = e.OriginalSource is DependencyObject source ? ItemsControl.ContainerFromElement(list, source) : null;
        var item = container is null ? null : list.ItemContainerGenerator.ItemFromContainer(container);
        if (NameOf(item) is not { } name || Window.GetWindow(list)?.DataContext is not MainViewModel vm || list.ContextMenu is not { } menu)
        {
            // not on a player: no menu
            e.Handled = true;
            return;
        }
        Fill(menu.Items, vm.PlayerActions, name);
    }

    /// <summary>The player of a list item, if it is one.</summary>
    public static string? NameOf(object? item) => item switch
    {
        string s when s.Length > 0 => s,
        BridgePlayer p => p.Username,
        SidebarPlayer s => s.Username,
        AccountRow a => a.Username,
        LogLineItem { Kind: LogLineKind.Chat, Line.Author: { Length: > 0 } author } => author,
        _ => null,
    };

    /// <summary>Opens the menu for a player at the pointer (the map's markers).</summary>
    public static void Open(MainViewModel vm, string player, UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.MousePoint };
        Fill(menu.Items, vm.PlayerActions, player);
        menu.IsOpen = true;
    }

    /// <summary>
    /// The actions on a player, as menu items: for the right-click menu, and for the character window
    /// (<paramref name="inWindow"/>: without the header and what the window itself shows).
    /// </summary>
    internal static void Fill(ItemCollection items, PlayerActions actions, string player, bool inWindow = false)
    {
        items.Clear();
        bool connected = actions.IsConnected;
        bool online = connected && actions.IsOnline(player);
        var bridge = actions.BridgeInfo(player);

        if (!inWindow)
        {
            items.Add(new MenuItem
            {
                Header = player + (!connected ? " (not connected)" : online ? "" : " (offline)"),
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            });
            Add(items, "Character details…", () => actions.ShowDetails(player), true,
                "Everything about this player in one window: health, skills, equipment, inventory and every action");
        }
        Add(items, "Show in the Players tab", () => actions.ShowInPlayersTab(player));
        Add(items, "Show on the map", () => actions.ShowOnMap(player), bridge?.X is not null,
            bridge is null ? "Needs the bridge (Bridge tab)" : null);
        items.Add(new Separator());

        var others = actions.OthersOnline(player);
        Sub(items, "Teleport to", online && others.Count > 0, others.Select(o => ((object)o, (Func<Task>)(() => actions.TeleportAsync(player, o)))));
        Sub(items, "Bring here", online && others.Count > 0, others.Select(o => ((object)o, (Func<Task>)(() => actions.TeleportAsync(o, player)))));
        Sub(items, "Give kit", online && actions.Kits.Count > 0,
            actions.Kits.Select(k => ((object)$"{k.Name} ({k.Summary})", (Func<Task>)(() => actions.GiveKitAsync(player, k)))));
        Sub(items, "Event", online, actions.PlayerEvents.Select(ev =>
            ((object)(ev.Id == "horde" ? $"{ev.Name} ({actions.HordeSize} zombies)" : ev.Name), (Func<Task>)(() => actions.EventAsync(player, ev)))));
        // why a bridge action is off: the player not online for RCON first, then what the bridge says
        string? Tip(string? problem, string? what) => !online ? "Needs the player online (connect RCON)" : problem ?? what;
        Add(items, "Heal completely…", () => actions.HealAsync(player), online && actions.HealProblem(player) is null,
            Tip(actions.HealProblem(player), null));
        Add(items, "Cure zombie infection…", () => actions.CureInfectionAsync(player), online && actions.CureProblem(player) is null,
            Tip(actions.CureProblem(player), "Ends the zombie infection of a bite or scratch, which Heal completely does not"));
        Add(items, "Their vehicle…", () => actions.ShowVehicle(player), online && actions.VehicleDetailsProblem(player) is null,
            Tip(actions.VehicleDetailsProblem(player), "The vehicle they are in, part by part: conditions, what is missing, a repair for each"));
        Add(items, "Repair their vehicle…", () => actions.RepairVehicleAsync(player), online && actions.VehicleProblem(player) is null,
            Tip(actions.VehicleProblem(player), "The vehicle they are in: every part whole, the ones that are gone (a wheel, a window, the battery...) put back"));
        // through the bridge (corpses: v6, the rest: v7): what RCON has no command for, within a radius
        // of where the player is now
        var cleanUp = new MenuItem { Header = "Clean up around", IsEnabled = online && actions.CorpsesProblem(player) is null, ToolTip = actions.CorpsesProblem(player) };
        ToolTipService.SetShowOnDisabled(cleanUp, true);
        Sub(cleanUp.Items, "Zombie corpses", true,
            actions.CorpseRadii.Select(r => ((object)$"{r} squares…", (Func<Task>)(() => actions.RemoveCorpsesAsync(player, r)))),
            "On every floor; the corpses of players and animals stay");
        Sub(cleanUp.Items, "Items on the ground", actions.AreaProblem is null,
            actions.CorpseRadii.Select(r => ((object)$"{r} squares…", (Func<Task>)(() => actions.RemoveGroundItemsAsync(player, r)))),
            actions.AreaProblem ?? "Counted first, then asked; what is in containers, on tables and shelves, or inside a safehouse is not touched");
        Sub(cleanUp.Items, "Fires", actions.AreaProblem is null,
            actions.CorpseRadii.Select(r => ((object)$"{r} squares", (Func<Task>)(() => actions.StopFiresAsync(player, r)))),
            actions.AreaProblem ?? "Puts out every fire in the area (lit campfires are left)");
        Sub(cleanUp.Items, "Wrecks", actions.WrecksProblem is null,
            actions.CorpseRadii.Select(r => ((object)$"{r} squares…", (Func<Task>)(() => actions.RemoveWrecksAsync(player, r)))),
            actions.WrecksProblem ?? "Burnt and smashed vehicles: counted first, then asked. Cars that can still be driven stay");
        items.Add(cleanUp);
        Add(items, "Hair and beard…", () => actions.SetHairAsync(player), online && actions.HairProblem(player) is null,
            Tip(actions.HairProblem(player), actions.HairTip));
        items.Add(new Separator());

        Sub(items, "Powers", online,
        [
            ("God mode on", () => actions.GodModeAsync(player, true)),
            ("God mode off", () => actions.GodModeAsync(player, false)),
            ("Invisible on", () => actions.InvisibleAsync(player, true)),
            ("Invisible off", () => actions.InvisibleAsync(player, false)),
            ("No clip on", () => actions.NoClipAsync(player, true)),
            ("No clip off", () => actions.NoClipAsync(player, false)),
        ]);
        Sub(items, "Access level", connected, actions.AccessLevels.Select(l => ((object)l, (Func<Task>)(() => actions.SetAccessLevelAsync(player, l)))));
        Sub(items, "Voice chat", online,
        [
            ("Mute", () => actions.VoiceAsync(player, true)),
            ("Unmute", () => actions.VoiceAsync(player, false)),
        ]);
        items.Add(new Separator());
        Add(items, "Kick…", () => actions.KickAsync(player), online);
        Add(items, "Ban…", () => actions.BanAsync(player), connected);
        Add(items, "Unban…", () => actions.UnbanAsync(player), connected);
        items.Add(new Separator());
        Add(items, "Copy name", () => actions.CopyName(player));
    }

    static void Add(ItemCollection items, string header, Action action, bool enabled = true, string? tip = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
        ToolTipService.SetShowOnDisabled(item, true);
        item.Click += (_, _) => action();
        items.Add(item);
    }

    static void Add(ItemCollection items, string header, Func<Task> action, bool enabled = true, string? tip = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
        ToolTipService.SetShowOnDisabled(item, true);
        item.Click += async (_, _) => await action();
        items.Add(item);
    }

    static void Sub(ItemCollection items, string header, bool enabled, IEnumerable<(object Header, Func<Task> Action)> children, string? tip = null)
    {
        var parent = new MenuItem { Header = header, IsEnabled = enabled, ToolTip = tip };
        ToolTipService.SetShowOnDisabled(parent, true);
        foreach (var (childHeader, action) in children)
        {
            var child = new MenuItem { Header = childHeader };
            child.Click += async (_, _) => await action();
            parent.Items.Add(child);
        }
        if (parent.Items.Count == 0)
            parent.IsEnabled = false;
        items.Add(parent);
    }
}

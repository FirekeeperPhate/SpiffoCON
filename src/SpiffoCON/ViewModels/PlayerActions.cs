using SpiffoCON.Core.Bridge;
using SpiffoCON.Core.Commands;
using SpiffoCON.Services;

namespace SpiffoCON.ViewModels;

/// <summary>
/// What the right-click menu on a player does, from any tab: RCON commands on that player (each reply in
/// the status bar), a kit, an event, and the bridge's heal, map and inventory.
/// </summary>
public sealed class PlayerActions(MainViewModel main)
{
    public bool IsConnected => main.IsSessionActive;

    public bool IsOnline(string player) => main.OnlinePlayers.Contains(player, StringComparer.OrdinalIgnoreCase);

    /// <summary>The other online players (teleport targets).</summary>
    public IReadOnlyList<string> OthersOnline(string player) =>
        main.OnlinePlayers.Where(p => !p.Equals(player, StringComparison.OrdinalIgnoreCase)).ToList();

    public BridgePlayer? BridgeInfo(string player) =>
        main.Bridge.Players.FirstOrDefault(p => p.Username.Equals(player, StringComparison.OrdinalIgnoreCase));

    public bool CanHeal(string player) => main.Bridge.CanAct && BridgeInfo(player) is not null;

    public IReadOnlyList<KitView> Kits => main.Kits.Kits;

    public IReadOnlyList<string> AccessLevels => PlayerCommands.AccessLevels;

    public IReadOnlyList<WorldEvent> PlayerEvents => EventCommands.Events.Where(e => e.Targeting == EventTargeting.Player).ToList();

    public int HordeSize => int.TryParse(main.Events.HordeSize.Trim(), out var n) ? Math.Clamp(n, 1, EventCommands.MaxHorde) : 20;

    async Task RunAsync(string player, string command, string label, string? confirm = null, bool refresh = false, string? other = null)
    {
        if (!main.IsSessionActive)
        {
            main.StatusText = "Not connected.";
            return;
        }
        if (confirm is not null && main.Confirm?.Invoke(confirm) != true)
            return;
        var reply = await main.RunAsync(command);
        var outcome = $"{label}: " + (reply is null
            ? "failed, see the console."
            : PlayerCommands.Interpret(reply, player, other) switch
            {
                CommandOutcome.Success => reply.Trim(),
                CommandOutcome.Failed => "failed: " + reply.Trim(),
                _ => "sent, the server gave no reply.",
            });
        // the refresh is a command too, and each command resets the status bar: the outcome goes last
        if (refresh)
            await main.RefreshPlayersAsync(quiet: true);
        main.StatusText = outcome;
    }

    public Task KickAsync(string player) =>
        RunAsync(player, PlayerCommands.Kick(player, null), $"Kick {player}", $"Kick {player}?\n\nThe Players tab can add a reason.", refresh: true);

    public Task BanAsync(string player) =>
        RunAsync(player, PlayerCommands.Ban(player, false, null), $"Ban {player}",
            $"Ban {player}?\n\nThe IP is not banned; the Players tab can add a reason and ban the IP too.", refresh: true);

    public Task UnbanAsync(string player) =>
        RunAsync(player, PlayerCommands.Unban(player), $"Unban {player}", $"Unban {player}?");

    public Task SetAccessLevelAsync(string player, string level) =>
        RunAsync(player, PlayerCommands.SetAccessLevel(player, level), $"{player} → {level}", $"Make {player} {level}?");

    public Task TeleportAsync(string player, string to) =>
        RunAsync(player, PlayerCommands.TeleportToPlayer(player, to), $"Teleport {player} to {to}", other: to);

    public Task GodModeAsync(string player, bool on) =>
        RunAsync(player, PlayerCommands.GodMode(player, on), $"God mode {(on ? "on" : "off")} for {player}");

    public Task InvisibleAsync(string player, bool on) =>
        RunAsync(player, PlayerCommands.Invisible(player, on), $"Invisible {(on ? "on" : "off")} for {player}");

    public Task NoClipAsync(string player, bool on) =>
        RunAsync(player, PlayerCommands.NoClip(player, on), $"No clip {(on ? "on" : "off")} for {player}");

    public Task VoiceAsync(string player, bool mute) =>
        RunAsync(player, PlayerCommands.VoiceBan(player, mute), $"Voice chat {(mute ? "muted" : "unmuted")} for {player}");

    public async Task GiveKitAsync(string player, KitView kit)
    {
        if (!main.IsSessionActive)
        {
            main.StatusText = "Not connected.";
            return;
        }
        if (kit.Items.Count == 0)
        {
            main.StatusText = $"The kit \"{kit.Name}\" is empty: add items from the Catalog tab.";
            return;
        }
        if (main.Confirm?.Invoke($"Give \"{kit.Name}\" ({kit.Summary}) to {player}?") != true)
            return;
        main.StatusText = $"Giving \"{kit.Name}\" to {player}...";
        var result = await main.Kits.GiveKitAsync(kit, [player]);
        main.Kits.Result = result;
        main.StatusText = result.ReplaceLineEndings(" · ");
    }

    public async Task EventAsync(string player, WorldEvent e)
    {
        if (!main.IsSessionActive)
        {
            main.StatusText = "Not connected.";
            return;
        }
        int horde = HordeSize;
        if (e.Id == "horde" && main.Confirm?.Invoke($"Spawn {horde} zombies around {player}?") != true)
            return;
        var reply = await main.RunAsync(EventCommands.Build(e, player, horde));
        main.StatusText = $"{e.Name} on {player}: " + (reply is null
            ? "failed, see the console."
            : EventCommands.Interpret(reply) switch
            {
                CommandOutcome.Success => reply.Trim(),
                CommandOutcome.Failed => "failed: " + reply.Trim(),
                _ => "sent, the server gave no reply.",
            });
    }

    /// <summary>Why the zombie corpses around a player can't be removed (null: they can).</summary>
    public string? CorpsesProblem(string player) =>
        main.Bridge.CorpsesProblem ?? (BridgeInfo(player)?.X is null ? "The bridge has no position for this player." : null);

    public IReadOnlyList<int> CorpseRadii => [10, 25, 50, 100];

    public async Task RemoveCorpsesAsync(string player, int radius)
    {
        if (BridgeInfo(player) is not { X: { } x, Y: { } y } info)
            return;
        if (await main.Bridge.RemoveCorpsesAsync(x, y, radius, info.Username) is { } result)
            main.StatusText = result;
    }

    public async Task HealAsync(string player)
    {
        if (BridgeInfo(player) is not { } info)
            return;
        await main.Bridge.HealCommand.ExecuteAsync(info);
        main.StatusText = main.Bridge.StatusText;
    }

    public void ShowInPlayersTab(string player)
    {
        main.Players.Target = player;
        if (IsOnline(player))
            main.Players.SelectedOnline = main.OnlinePlayers.First(p => p.Equals(player, StringComparison.OrdinalIgnoreCase));
        main.ShowTab("Players");
    }

    public void ShowOnMap(string player)
    {
        main.ShowTab("Map");
        main.Map.Show(BridgeInfo(player)?.Username ?? player);
    }

    public void ShowInventory(string player)
    {
        main.ShowTab("Bridge");
        main.Bridge.SelectedPlayer = BridgeInfo(player);
    }

    public void CopyName(string player)
    {
        SafeClipboard.SetText(player);
        main.StatusText = $"Copied: {player}";
    }
}

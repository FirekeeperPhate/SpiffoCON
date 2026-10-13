using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Bridge;

namespace SpiffoCON.ViewModels;

/// <summary>
/// What stands on the squares around a player (bridge v18): furniture, walls, doors, lights. For the object
/// the game no longer lets anybody pick up or dismantle: it is listed here and removed as the game's own
/// admin tool does.
/// </summary>
public sealed partial class ObjectsViewModel : ObservableObject
{
    readonly MainViewModel _main;
    bool _reading;

    public ObjectsViewModel(MainViewModel main, string username)
    {
        _main = main;
        Username = username;
        _ = RefreshAsync();
    }

    public string Username { get; }

    public string Title => $"Objects around {Username}";

    /// <summary>For the status line of the window.</summary>
    public MainViewModel Main => _main;

    public ObservableCollection<BridgeObject> Objects { get; } = [];

    /// <summary>Squares on each side of the player's; the bridge goes no further than the last.</summary>
    public IReadOnlyList<int> Radii { get; } = [1, 2, 3, 4, 6];

    [ObservableProperty] private int radius = 2;

    /// <summary>The floor of each square too: left out at first, there is one on every square.</summary>
    [ObservableProperty] private bool floors;

    /// <summary>How many, around where, read when; or why there is nothing to show.</summary>
    [ObservableProperty] private string summary = "Asking the bridge...";

    partial void OnRadiusChanged(int value) => _ = RefreshAsync();
    partial void OnFloorsChanged(bool value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_reading)
            return;
        _reading = true;
        try
        {
            var (read, problem) = await _main.Bridge.ReadObjectsAsync(Username, Radius, Floors);
            Objects.Clear();
            if (read is null)
            {
                Summary = problem ?? "Nothing read.";
                return;
            }
            // the nearest first: what one stands at is what one is looking for
            foreach (var o in read.Objects
                         .OrderBy(o => Math.Max(Math.Abs(o.X - read.X), Math.Abs(o.Y - read.Y)))
                         .ThenBy(o => o.Y).ThenBy(o => o.X).ThenBy(o => o.Index))
                Objects.Add(o);
            Summary = (read.Objects.Count == 0 ? "No objects" : $"{read.Objects.Count} object{(read.Objects.Count == 1 ? "" : "s")}")
                + $" within {read.Radius} square{(read.Radius == 1 ? "" : "s")} of {Username} ({read.X}, {read.Y}, floor {read.Z}), the nearest first"
                + $" · read at {DateTime.Now:HH:mm:ss}";
        }
        finally
        {
            _reading = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(BridgeObject? o)
    {
        if (o is null)
            return;
        if (await _main.Bridge.RemoveObjectAsync(o) is not { } result)
            return;
        await RefreshAsync();
        _main.StatusText = result;
    }

    [RelayCommand]
    private void CopySprite(BridgeObject? o)
    {
        if (o?.Sprite is { Length: > 0 } sprite)
        {
            Services.SafeClipboard.SetText(sprite);
            _main.StatusText = $"Copied: {sprite}";
        }
    }
}
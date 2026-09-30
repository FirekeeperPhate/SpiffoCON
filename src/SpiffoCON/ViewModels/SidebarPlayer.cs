using CommunityToolkit.Mvvm.ComponentModel;

namespace SpiffoCON.ViewModels;

/// <summary>An online player in the window's sidebar, with where the bridge last saw them.</summary>
public sealed partial class SidebarPlayer(string username) : ObservableObject
{
    public string Username { get; } = username;

    /// <summary>Position (and floor, or dead) from the bridge; null without the bridge.</summary>
    [ObservableProperty] private string? detail;
}

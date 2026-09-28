namespace SpiffoCON.Core.Steam;

public enum ModUpdateState
{
    /// <summary>The server's copy is as new as Steam's.</summary>
    UpToDate,

    /// <summary>Steam has a newer version than the server's copy: a restart makes the server download it.</summary>
    UpdateAvailable,

    /// <summary>In WorkshopItems= but not installed on the server yet (it downloads it at the next start).</summary>
    NotOnServer,

    /// <summary>Steam doesn't list it publicly (unlisted, private or removed), so it can't be compared.</summary>
    Unknown,
}

public sealed record ModUpdateStatus(string WorkshopId, string Title, DateTimeOffset? OnServer, DateTimeOffset? OnSteam, ModUpdateState State);

/// <summary>
/// Compares the server's workshop manifest (what it installed) with Steam (what is published).
/// B42's own checkModsNeedUpdate asks Steam from the server and only writes a yes/no to its log.
/// </summary>
public static class ModUpdates
{
    public static IReadOnlyList<ModUpdateStatus> Compare(
        IEnumerable<string> workshopIds,
        IReadOnlyDictionary<string, InstalledWorkshopItem> installed,
        IReadOnlyDictionary<string, WorkshopItemInfo> steam)
    {
        var result = new List<ModUpdateStatus>();
        foreach (var id in workshopIds.Distinct())
        {
            var published = steam.GetValueOrDefault(id) is { Exists: true } info ? info : null;
            DateTimeOffset? onServer = installed.TryGetValue(id, out var item) && item.TimeUpdated > 0
                ? DateTimeOffset.FromUnixTimeSeconds(item.TimeUpdated)
                : null;
            var state = item is null ? ModUpdateState.NotOnServer
                : published is null ? ModUpdateState.Unknown
                : published.Updated.ToUnixTimeSeconds() > item.TimeUpdated ? ModUpdateState.UpdateAvailable
                : ModUpdateState.UpToDate;
            result.Add(new ModUpdateStatus(id, published?.Title ?? id, onServer, published?.Updated, state));
        }
        // what needs attention first
        return result.OrderBy(r => r.State switch
        {
            ModUpdateState.UpdateAvailable => 0,
            ModUpdateState.NotOnServer => 1,
            ModUpdateState.Unknown => 2,
            _ => 3,
        }).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

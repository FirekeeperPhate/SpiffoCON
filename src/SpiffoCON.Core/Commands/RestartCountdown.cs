namespace SpiffoCON.Core.Commands;

/// <summary>When to warn players before a restart, and what to tell them.</summary>
public static class RestartCountdown
{
    /// <summary>Warning points, in seconds before the restart.</summary>
    static readonly int[] Points = [3600, 2700, 1800, 1200, 900, 600, 300, 180, 120, 60, 30, 10];

    public const string TimePlaceholder = "{time}";

    public const string DefaultMessage = "<RGB:1,0.55,0.1>Server restart in {time}. Find a safe spot and log out.";

    /// <summary>The seconds-before-restart at which to warn, starting with the whole delay.</summary>
    public static IReadOnlyList<int> WarningsFor(int totalSeconds)
    {
        var list = new List<int> { totalSeconds };
        list.AddRange(Points.Where(p => p < totalSeconds));
        return list;
    }

    /// <summary>"10 minutes", "1 minute", "30 seconds", "1 hour 30 minutes".</summary>
    public static string Describe(int seconds)
    {
        if (seconds < 60)
            return seconds == 1 ? "1 second" : $"{seconds} seconds";
        int minutes = (seconds + 30) / 60;
        if (minutes < 60)
            return minutes == 1 ? "1 minute" : $"{minutes} minutes";
        int hours = minutes / 60, rest = minutes % 60;
        var text = hours == 1 ? "1 hour" : $"{hours} hours";
        return rest == 0 ? text : text + (rest == 1 ? " 1 minute" : $" {rest} minutes");
    }

    /// <summary>The message with {time} filled in (appended when the template has no placeholder).</summary>
    public static string Message(string template, int secondsLeft)
    {
        var time = Describe(secondsLeft);
        if (string.IsNullOrWhiteSpace(template))
            template = DefaultMessage;
        return template.Contains(TimePlaceholder, StringComparison.OrdinalIgnoreCase)
            ? template.Replace(TimePlaceholder, time, StringComparison.OrdinalIgnoreCase)
            : $"{template.TrimEnd()} ({time})";
    }
}

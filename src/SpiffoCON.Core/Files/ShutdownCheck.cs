using System.Text;

namespace SpiffoCON.Core.Files;

/// <summary>How the run before the current one ended, read from its own log.</summary>
/// <param name="File">The log of that run, as the folder names it ("logs_2026-10-10/2026-10-10_09-45_DebugLog-server.txt").</param>
/// <param name="Started">When that run started (from the name of its log).</param>
/// <param name="Ended">The time of the last line it wrote, when it has one.</param>
/// <param name="Clean">The log ends with the server's own shutdown lines.</param>
/// <param name="CurrentFile">The log of the run going on now: what tells one check from the next.</param>
/// <param name="LastLines">The last lines of that log, for whoever wants to see what it was doing.</param>
public sealed record PreviousRun(string File, DateTime Started, DateTime? Ended, bool Clean, string CurrentFile, IReadOnlyList<string> LastLines);

/// <summary>
/// Tells a crash from a restart. A server that stops as it should ends its debug log with
/// "Shutdown handling started ... Saving finish ... Shutdown handling finished" (the JVM's shutdown
/// hook, which also saves the world); one that crashed, was killed or lost power just stops writing. At
/// its next start the server moves that log into a logs_&lt;date&gt; folder, where it is read from.
/// </summary>
public static class ShutdownCheck
{
    public const string LogType = "DebugLog-server";

    /// <summary>What the server's shutdown hook prints last.</summary>
    public const string CleanMarker = "Shutdown handling finished";

    /// <summary>How much of the end of the log is read: the shutdown lines are its last twenty or so.</summary>
    const int TailBytes = 8 * 1024;

    /// <summary>
    /// The run before the one going on, or null when there is none to judge: no run going on (no debug log
    /// in the folder), or no earlier log kept.
    /// </summary>
    public static async Task<PreviousRun?> CheckAsync(ILogFolder folder, CancellationToken ct = default)
    {
        var current = (await folder.ListAsync(ct).ConfigureAwait(false))
            .Where(f => f.Type.Equals(LogType, StringComparison.OrdinalIgnoreCase))
            .MaxBy(f => f.Started);
        if (current is null)
            return null;
        // the newest of the logs put away that started before this run (same minute: a restart within it)
        var previous = (await folder.ListArchivedAsync(2, ct).ConfigureAwait(false))
            .Where(f => f.Type.Equals(LogType, StringComparison.OrdinalIgnoreCase) && f.Started <= current.Started)
            .OrderByDescending(f => f.Started).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (previous is null)
            return null;

        long from = Math.Max(0, previous.Size - TailBytes);
        var bytes = await folder.ReadAsync(previous.Name, from, TailBytes, ct).ConfigureAwait(false);
        var lines = Encoding.UTF8.GetString(bytes).ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // read from the middle of the file: the first line is a piece of one
        if (from > 0 && lines.Length > 0)
            lines = lines[1..];
        bool clean = lines.Any(l => l.Contains(CleanMarker, StringComparison.OrdinalIgnoreCase));
        DateTime? ended = lines.Reverse().Select(l => LogTail.Parse(l).Time).FirstOrDefault(t => t is not null);
        return new PreviousRun(previous.Name, previous.Started, ended, clean, current.Name, lines.TakeLast(3).ToList());
    }
}

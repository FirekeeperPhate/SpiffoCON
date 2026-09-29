using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpiffoCON.Core.Files;

public enum LogLineKind
{
    /// <summary>A player's chat message.</summary>
    Chat,
    /// <summary>A broadcast (servermsg / server alert).</summary>
    Alert,
    /// <summary>Anything else the server wrote.</summary>
    Server,
    /// <summary>Inserted by SpiffoCON ("new log file").</summary>
    Marker,
}

public sealed record LogLine(DateTime? Time, string Level, string Text, LogLineKind Kind, string? Channel = null, string? Author = null);

/// <summary>
/// Follows one type of log ("chat", "user", "admin"...): reads what was added since the last poll,
/// switches to the new file when the server restarts, and parses the lines.
/// </summary>
public sealed partial class LogTail(ILogFolder folder, string type)
{
    /// <summary>How much of the current file to show when starting.</summary>
    public int InitialBytes { get; init; } = 128 * 1024;

    public int MaxBytesPerPoll { get; init; } = 1024 * 1024;

    public string Type { get; } = type;

    public string? CurrentFile { get; private set; }

    /// <summary>
    /// The last poll returned older lines, not news: the first read, or a new file found after a
    /// long gap without polls (following paused, connection down) while the server restarted.
    /// </summary>
    public bool LastPollWasBacklog { get; private set; }

    /// <summary>A new file seen within this time of the last poll is followed from its start.</summary>
    public TimeSpan FreshFileWindow { get; init; } = TimeSpan.FromMinutes(1);

    DateTime _lastPoll = DateTime.MinValue;

    long _offset;
    byte[] _partial = [];

    /// <summary>New complete lines since the last call (the first call returns the file's tail).</summary>
    public async Task<IReadOnlyList<LogLine>> PollAsync(CancellationToken ct = default)
    {
        var lines = new List<LogLine>();
        LastPollWasBacklog = false;
        var newest = (await folder.ListAsync(ct).ConfigureAwait(false))
            .Where(f => f.Type.Equals(Type, StringComparison.OrdinalIgnoreCase))
            .MaxBy(f => f.Started);
        var sinceLastPoll = DateTime.Now - _lastPoll;
        _lastPoll = DateTime.Now;
        if (newest is null)
            return lines;

        if (newest.Name != CurrentFile)
        {
            if (CurrentFile is not null)
                lines.Add(new LogLine(DateTime.Now, "", $"New log file {newest.Name} (the server restarted)", LogLineKind.Marker));
            bool first = CurrentFile is null;
            CurrentFile = newest.Name;
            // a restart seen live: the new file is news, from its first line (mod errors at start-up);
            // after a long gap it was written while nobody watched: only its tail, as the past
            LastPollWasBacklog = first || sinceLastPoll > FreshFileWindow;
            _offset = LastPollWasBacklog ? Math.Max(0, newest.Size - InitialBytes) : 0;
            _partial = [];
            if (_offset > 0)
                _partial = [(byte)'\u0001']; // the first line is cut: drop it
        }
        else if (newest.Size < _offset)
        {
            _offset = 0; // truncated
            _partial = [];
        }
        else if (sinceLastPoll > FreshFileWindow)
        {
            // back after a long gap (following paused, link down, PC asleep): what was written
            // meanwhile is the past, not news; if it is a lot, only its tail is worth reading
            LastPollWasBacklog = true;
            if (newest.Size - _offset > InitialBytes)
            {
                _offset = newest.Size - InitialBytes;
                _partial = [(byte)'\u0001'];
                lines.Add(new LogLine(DateTime.Now, "", "Earlier lines skipped (written while SpiffoCON was not following)", LogLineKind.Marker));
            }
        }
        if (newest.Size <= _offset)
            return lines;

        var bytes = await folder.ReadAsync(newest.Name, _offset, MaxBytesPerPoll, ct).ConfigureAwait(false);
        _offset += bytes.Length;
        var data = _partial.Length > 0 ? [.. _partial, .. bytes] : bytes;

        // only complete lines: '\n' never occurs inside a UTF-8 multi-byte sequence
        int end = Array.LastIndexOf(data, (byte)'\n');
        if (end < 0)
        {
            _partial = data;
            return lines;
        }
        _partial = data[(end + 1)..];
        var text = Encoding.UTF8.GetString(data, 0, end);
        var split = text.Split('\n');
        int start = split.Length > 0 && split[0].StartsWith('\u0001') ? 1 : 0;
        for (int i = start; i < split.Length; i++)
        {
            var line = split[i].TrimEnd('\r');
            if (line.Length > 0)
                lines.Add(Parse(line));
        }
        return lines;
    }

    /// <summary>
    /// "[28-09-26 12:27:54.518][info] Got message:ChatMessage{chat=General, author='rj', text='hi'}."
    /// (format from zombie.chat.ChatMessage.toString in the B42 server).
    /// </summary>
    public static LogLine Parse(string line)
    {
        var m = Prefix().Match(line);
        if (!m.Success)
            return new LogLine(null, "", line, LogLineKind.Server);
        DateTime? time = DateTime.TryParseExact(m.Groups["time"].Value, "dd-MM-yy HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
        var level = m.Groups["level"].Value;
        var rest = m.Groups["rest"].Value;

        var chat = ChatMessage().Match(rest);
        if (chat.Success)
            return new LogLine(time, level, chat.Groups["text"].Value, LogLineKind.Chat, chat.Groups["chat"].Value.Trim(), chat.Groups["author"].Value);
        var alert = ServerAlert().Match(rest);
        if (alert.Success)
            return new LogLine(time, level, PlainMessage(alert.Groups["text"].Value), LogLineKind.Alert);
        return new LogLine(time, level, rest, LogLineKind.Server);
    }

    [GeneratedRegex(@"^\[(?<time>\d\d-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)\](?:\[(?<level>[^\]]*)\])? ?(?<rest>.*)$")]
    private static partial Regex Prefix();

    [GeneratedRegex(@"^Got message:ChatMessage\{chat=(?<chat>[^,]*), author='(?<author>.*?)', text='(?<text>.*)'\}\.?$")]
    private static partial Regex ChatMessage();

    /// <summary>A broadcast as players read it: color tags dropped, &lt;LINE&gt; as a separator.</summary>
    static string PlainMessage(string markup) =>
        ColorTag().Replace(markup, "").Replace(" <LINE> ", " / ").Replace("<LINE>", " / ").Trim();

    [GeneratedRegex(@"<RGB:[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ColorTag();

    [GeneratedRegex(@"^Server alert message: '(?<text>.*)' sent\.*$")]
    private static partial Regex ServerAlert();
}

using System.Globalization;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace SpiffoCON.Core.Files;

/// <summary>
/// A server log file: Zomboid/Logs/&lt;yyyy-MM-dd_HH-mm&gt;_&lt;type&gt;.txt (chat, user, admin, pvp,
/// DebugLog-server, ...). The server starts new files at every start and moves the old ones into
/// logs_&lt;date&gt; folders.
/// </summary>
public sealed partial record LogFileInfo(string Name, string Type, DateTime Started, long Size)
{
    public static LogFileInfo? FromName(string name, long size)
    {
        var m = NamePattern().Match(name);
        if (!m.Success)
            return null;
        // TryParse: one oddly named file ("2026-13-45_...") must not break the whole log viewer
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var started))
            return null;
        return new LogFileInfo(name, m.Groups[2].Value, started, size);
    }

    // [0-9], not \d: \d also matches other scripts' digits
    [GeneratedRegex(@"^([0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2})_(.+)\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();
}

/// <summary>The Logs folder, on the server (SFTP) or on this PC.</summary>
public interface ILogFolder : IDisposable
{
    string Description { get; }

    Task<IReadOnlyList<LogFileInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>Up to <paramref name="maxBytes"/> bytes from <paramref name="offset"/>.</summary>
    Task<byte[]> ReadAsync(string name, long offset, int maxBytes, CancellationToken ct = default);

    /// <summary>
    /// The logs of earlier runs, which the server moves into logs_&lt;date&gt; folders at each start: those of
    /// the newest <paramref name="folders"/> of them. Their names carry the folder
    /// ("logs_2026-10-10/2026-10-10_09-45_chat.txt") and can be read with <see cref="ReadAsync"/>.
    /// </summary>
    Task<IReadOnlyList<LogFileInfo>> ListArchivedAsync(int folders, CancellationToken ct = default);
}

/// <summary>What the two kinds of folder share about the logs put away.</summary>
static class ArchivedLogs
{
    public static bool IsArchive(string name) => name.StartsWith("logs_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The newest folders: their names are dates, so the order of the names is the order of time.</summary>
    public static IEnumerable<string> Newest(IEnumerable<string> names, int folders) =>
        names.Where(IsArchive).OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, folders));

    public static LogFileInfo? InFolder(string folder, string file, long size) =>
        LogFileInfo.FromName(file, size) is { } info ? info with { Name = folder + "/" + file } : null;
}

public sealed class LocalLogFolder(string path) : ILogFolder
{
    public string Description => "Folder: " + path;

    public Task<IReadOnlyList<LogFileInfo>> ListAsync(CancellationToken ct = default)
    {
        // a directory listing can show a stale size for a file the server keeps appending to; the
        // tail would then think it shrank and read it all again: ask the open file instead
        IReadOnlyList<LogFileInfo> files = new DirectoryInfo(path).EnumerateFiles("*.txt")
            .Select(f => LogFileInfo.FromName(f.Name, CurrentSize(f)))
            .OfType<LogFileInfo>()
            .ToList();
        return Task.FromResult(files);
    }

    static long CurrentSize(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return file.Length;
        }
    }

    public async Task<byte[]> ReadAsync(string name, long offset, int maxBytes, CancellationToken ct = default)
    {
        // the server keeps the file open for writing
        await using var stream = new FileStream(Path.Combine(path, name), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(maxBytes, Math.Max(0, stream.Length - offset))];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    public Task<IReadOnlyList<LogFileInfo>> ListArchivedAsync(int folders, CancellationToken ct = default)
    {
        var files = new List<LogFileInfo>();
        var root = new DirectoryInfo(path);
        foreach (var name in ArchivedLogs.Newest(root.EnumerateDirectories().Select(d => d.Name), folders))
            foreach (var f in new DirectoryInfo(Path.Combine(path, name)).EnumerateFiles("*.txt"))
                if (ArchivedLogs.InFolder(name, f.Name, f.Length) is { } info)
                    files.Add(info);
        return Task.FromResult<IReadOnlyList<LogFileInfo>>(files);
    }

    public void Dispose() { }
}

/// <summary>Logs over SFTP; one connection is kept open while tailing and reopened if it drops.</summary>
public sealed class SftpLogFolder(SftpSettings settings, string remotePath) : ILogFolder
{
    SftpClient? _client;
    SftpSettings _settings = settings;
    bool _disposed;

    public string Description => $"SFTP {_settings.Host}: {remotePath}";

    async Task<SftpClient> ClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
            return _client;
        // forget the old client before reconnecting: if this attempt fails, the next one must
        // not find a disposed client (IsConnected throws then) and never try again
        _client?.Dispose();
        _client = null;
        var (client, fingerprint) = await SftpProbe.ConnectAsync(_settings, ct).ConfigureAwait(false);
        if (_disposed)
        {
            // closed while connecting: this session must not stay open
            client.Dispose();
            throw new ObjectDisposedException(GetType().Name);
        }
        // reconnections insist on the key seen now
        _settings = _settings.Pinned(fingerprint);
        _client = client;
        return client;
    }

    public async Task<IReadOnlyList<LogFileInfo>> ListAsync(CancellationToken ct = default)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var files = new List<LogFileInfo>();
        await foreach (var f in client.ListDirectoryAsync(remotePath, ct).ConfigureAwait(false))
            if (f.IsRegularFile && LogFileInfo.FromName(f.Name, f.Length) is { } info)
                files.Add(info);
        return files;
    }

    public async Task<IReadOnlyList<LogFileInfo>> ListArchivedAsync(int folders, CancellationToken ct = default)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var root = remotePath.TrimEnd('/');
        var names = new List<string>();
        await foreach (var f in client.ListDirectoryAsync(root, ct).ConfigureAwait(false))
            if (f.IsDirectory && ArchivedLogs.IsArchive(f.Name))
                names.Add(f.Name);
        var files = new List<LogFileInfo>();
        foreach (var name in ArchivedLogs.Newest(names, folders))
            await foreach (var f in client.ListDirectoryAsync(root + "/" + name, ct).ConfigureAwait(false))
                if (f.IsRegularFile && ArchivedLogs.InFolder(name, f.Name, f.Length) is { } info)
                    files.Add(info);
        return files;
    }

    public async Task<byte[]> ReadAsync(string name, long offset, int maxBytes, CancellationToken ct = default)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        await using var stream = await client.OpenAsync(remotePath.TrimEnd('/') + "/" + name, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
        stream.Seek(offset, SeekOrigin.Begin);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        while (buffer.Length < maxBytes)
        {
            int n = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maxBytes - buffer.Length)), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();
    }
}

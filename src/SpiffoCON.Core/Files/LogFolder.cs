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
        var started = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH-mm", CultureInfo.InvariantCulture);
        return new LogFileInfo(name, m.Groups[2].Value, started, size);
    }

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}_\d{2}-\d{2})_(.+)\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex NamePattern();
}

/// <summary>The Logs folder, on the server (SFTP) or on this PC.</summary>
public interface ILogFolder : IDisposable
{
    string Description { get; }

    Task<IReadOnlyList<LogFileInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>Up to <paramref name="maxBytes"/> bytes from <paramref name="offset"/>.</summary>
    Task<byte[]> ReadAsync(string name, long offset, int maxBytes, CancellationToken ct = default);
}

public sealed class LocalLogFolder(string path) : ILogFolder
{
    public string Description => "Folder: " + path;

    public Task<IReadOnlyList<LogFileInfo>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<LogFileInfo> files = new DirectoryInfo(path).EnumerateFiles("*.txt")
            .Select(f => LogFileInfo.FromName(f.Name, f.Length))
            .OfType<LogFileInfo>()
            .ToList();
        return Task.FromResult(files);
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

    public void Dispose() { }
}

/// <summary>Logs over SFTP; one connection is kept open while tailing and reopened if it drops.</summary>
public sealed class SftpLogFolder(SftpSettings settings, string remotePath) : ILogFolder
{
    SftpClient? _client;

    public string Description => $"SFTP {settings.Host}: {remotePath}";

    async Task<SftpClient> ClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
            return _client;
        _client?.Dispose();
        (_client, _) = await SftpProbe.ConnectAsync(settings, ct).ConfigureAwait(false);
        return _client;
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

    public void Dispose() => _client?.Dispose();
}

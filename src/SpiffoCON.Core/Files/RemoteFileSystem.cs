using Renci.SshNet;
using Renci.SshNet.Common;

namespace SpiffoCON.Core.Files;

public sealed record RemoteEntry(string Name, string FullName, bool IsDirectory, bool IsFile, long Length, DateTime LastWriteTimeUtc);

/// <summary>The few remote operations the mod fetcher needs; one instance is one connection.</summary>
public interface IRemoteFileSystem : IDisposable
{
    /// <summary>The entries of a folder; null when it doesn't exist or can't be read.</summary>
    Task<IReadOnlyList<RemoteEntry>?> ListAsync(string path, CancellationToken ct);

    Task DownloadAsync(string path, Stream target, CancellationToken ct);
}

public sealed class SftpRemoteFileSystem(SftpClient client) : IRemoteFileSystem
{
    public static async Task<IRemoteFileSystem> ConnectAsync(SftpSettings settings, CancellationToken ct)
    {
        var (client, _) = await SftpProbe.ConnectAsync(settings, ct).ConfigureAwait(false);
        return new SftpRemoteFileSystem(client);
    }

    public async Task<IReadOnlyList<RemoteEntry>?> ListAsync(string path, CancellationToken ct)
    {
        var entries = new List<RemoteEntry>();
        try
        {
            await foreach (var f in client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
                if (f.Name is not "." and not "..")
                    entries.Add(new RemoteEntry(f.Name, f.FullName, f.IsDirectory, f.IsRegularFile, f.Length, f.LastWriteTimeUtc));
        }
        catch (Exception ex) when (ex is SftpPermissionDeniedException or SftpPathNotFoundException)
        {
            return null;
        }
        return entries;
    }

    public Task DownloadAsync(string path, Stream target, CancellationToken ct) => client.DownloadFileAsync(path, target, ct);

    public void Dispose() => client.Dispose();
}

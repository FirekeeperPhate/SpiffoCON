using System.Text;
using Renci.SshNet.Common;

namespace SpiffoCON.Core.Files;

/// <summary>Where the SandboxVars.lua being edited lives.</summary>
public interface ISandboxStore
{
    /// <summary>For the UI ("SFTP: /home/.../Server/pzserver_SandboxVars.lua").</summary>
    string Description { get; }

    /// <summary>Short name for backups (host or file name).</summary>
    string BackupLabel { get; }

    Task<string> ReadAsync(CancellationToken ct = default);

    Task WriteAsync(string text, CancellationToken ct = default);
}

/// <summary>Reads and writes text the way the game writes it: UTF-8, BOM only if the file had one.</summary>
static class SandboxText
{
    public static (string Text, bool Bom) Decode(byte[] bytes)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var body = bom ? bytes.AsSpan(3) : bytes;
        try
        {
            return (new UTF8Encoding(false, true).GetString(body), bom);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(body), bom);
        }
    }

    public static byte[] Encode(string text, bool bom) => new UTF8Encoding(bom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(text)).ToArray();
}

/// <summary>A file on this PC: a local server, or a copy downloaded from the host's panel.</summary>
public sealed class LocalSandboxStore(string path) : ISandboxStore
{
    bool _bom;

    public string Path { get; } = path;
    public string Description => "File: " + Path;
    public string BackupLabel => System.IO.Path.GetFileNameWithoutExtension(Path);

    public async Task<string> ReadAsync(CancellationToken ct = default)
    {
        var (text, bom) = SandboxText.Decode(await File.ReadAllBytesAsync(Path, ct).ConfigureAwait(false));
        _bom = bom;
        return text;
    }

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        var temp = Path + ".spiffocon-new";
        await File.WriteAllBytesAsync(temp, SandboxText.Encode(text, _bom), ct).ConfigureAwait(false);
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>The server's file over SFTP. Writes go to a temporary file renamed over the original.</summary>
public sealed class SftpSandboxStore(SftpSettings settings, string remotePath) : ISandboxStore
{
    bool _bom;

    public string RemotePath { get; } = remotePath;
    public string Description => $"SFTP {settings.Host}: {RemotePath}";
    public string BackupLabel => settings.Host + "_" + System.IO.Path.GetFileNameWithoutExtension(RemotePath);

    public async Task<string> ReadAsync(CancellationToken ct = default)
    {
        var (client, _) = await SftpProbe.ConnectAsync(settings, ct).ConfigureAwait(false);
        using (client)
        {
            using var buffer = new MemoryStream();
            await client.DownloadFileAsync(RemotePath, buffer, ct).ConfigureAwait(false);
            var (text, bom) = SandboxText.Decode(buffer.ToArray());
            _bom = bom;
            return text;
        }
    }

    public async Task WriteAsync(string text, CancellationToken ct = default)
    {
        var bytes = SandboxText.Encode(text, _bom);
        var (client, _) = await SftpProbe.ConnectAsync(settings, ct).ConfigureAwait(false);
        using (client)
        {
            var temp = RemotePath + ".spiffocon-new";
            using (var upload = new MemoryStream(bytes))
                await client.UploadFileAsync(upload, temp, ct).ConfigureAwait(false);
            try
            {
                // atomic replace where the server supports posix-rename (OpenSSH does)
                client.RenameFile(temp, RemotePath, isPosix: true);
            }
            catch (Exception ex) when (ex is SshException or NotSupportedException)
            {
                using (var upload = new MemoryStream(bytes))
                    await client.UploadFileAsync(upload, RemotePath, ct).ConfigureAwait(false);
                try { await client.DeleteFileAsync(temp, ct).ConfigureAwait(false); } catch (SshException) { }
            }
        }
    }
}

public static class SandboxFiles
{
    /// <summary>Server/&lt;name&gt;.ini → Server/&lt;name&gt;_SandboxVars.lua (same folder, remote or local).</summary>
    public static string ForServerConfig(string iniPath)
    {
        int slash = Math.Max(iniPath.LastIndexOf('/'), iniPath.LastIndexOf('\\'));
        var folder = slash >= 0 ? iniPath[..(slash + 1)] : "";
        var name = iniPath[(slash + 1)..];
        if (name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return folder + name + "_SandboxVars.lua";
    }

    /// <summary>Keeps a copy of the text before it is replaced; returns the backup's path.</summary>
    public static string Backup(string backupFolder, string label, string text)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            label = label.Replace(c, '_');
        Directory.CreateDirectory(backupFolder);
        var path = Path.Combine(backupFolder, $"{label}_{DateTime.Now:yyyyMMdd-HHmmss}.lua");
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
}

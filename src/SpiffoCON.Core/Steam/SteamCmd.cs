using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace SpiffoCON.Core.Steam;

/// <summary>
/// Downloads workshop items anonymously with Valve's SteamCMD, which SpiffoCON installs on first use
/// into its own folder. This is the fallback when the host gives no file access and the mods are
/// not in the local Steam folder.
/// </summary>
public sealed partial class SteamCmd(string folder, HttpClient http)
{
    const string DownloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    public string Folder { get; } = folder;

    string Exe => Path.Combine(Folder, OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");

    /// <summary>Where SteamCMD puts workshop item &lt;id&gt;.</summary>
    public string WorkshopFolder => Path.Combine(Folder, "steamapps", "workshop", "content", SteamLocator.ZomboidAppId);

    public bool IsInstalled => File.Exists(Exe);

    public async Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        if (IsInstalled)
            return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Automatic SteamCMD install is only implemented for Windows.");
        Directory.CreateDirectory(Folder);
        var zip = Path.Combine(Folder, "steamcmd.zip");
        await using (var file = File.Create(zip))
        await using (var download = await http.GetStreamAsync(DownloadUrl, ct).ConfigureAwait(false))
            await download.CopyToAsync(file, ct).ConfigureAwait(false);
        ZipFile.ExtractToDirectory(zip, Folder, overwriteFiles: true);
        File.Delete(zip);
    }

    /// <summary>
    /// Downloads the items in one SteamCMD session. Returns id → folder for the ones that succeeded.
    /// The first run also updates SteamCMD itself, which takes a minute.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> DownloadAsync(
        IReadOnlyCollection<string> workshopIds, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await EnsureInstalledAsync(ct).ConfigureAwait(false);
        var args = new List<string> { "+login", "anonymous" };
        foreach (var id in workshopIds)
            args.AddRange(["+workshop_download_item", SteamLocator.ZomboidAppId, id]);
        args.Add("+quit");

        var psi = new ProcessStartInfo(Exe)
        {
            WorkingDirectory = Folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var done = new Dictionary<string, string>();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("SteamCMD did not start.");
        using var kill = ct.Register(() => { try { process.Kill(true); } catch { } });
        _ = process.StandardError.ReadToEndAsync(ct);
        while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            var m = Downloaded().Match(line);
            if (m.Success)
            {
                done[m.Groups[1].Value] = Path.Combine(WorkshopFolder, m.Groups[1].Value);
                progress?.Report($"Downloaded workshop item {m.Groups[1].Value}");
            }
            else if (line.Contains("ERROR", StringComparison.Ordinal) || line.Contains("Update state", StringComparison.Ordinal))
            {
                progress?.Report(line.Trim());
            }
        }
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return done;
    }

    [GeneratedRegex(@"Success\. Downloaded item (\d+)")]
    private static partial Regex Downloaded();
}

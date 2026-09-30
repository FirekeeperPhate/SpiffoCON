using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SpiffoCON.Core.Updates;

/// <summary>How this copy of SpiffoCON was installed: which installer can update it.</summary>
public enum InstallKind
{
    /// <summary>By the installer, for every user of the PC (needs the administrator prompt).</summary>
    AllUsers,

    /// <summary>By the installer, for the current user only (the default).</summary>
    CurrentUser,

    /// <summary>Anything else (a build, a copied folder): the releases page is opened instead.</summary>
    Other,
}

/// <summary>A file attached to a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>The latest GitHub release: its version, notes, files and when it was published.</summary>
public sealed record ReleaseInfo(Version Version, string Notes, IReadOnlyList<ReleaseAsset> Assets, DateTime? Published = null);

/// <summary>
/// Looks for a newer release on GitHub (the repository's latest release: drafts and pre-releases are not
/// "latest"), downloads its installer and runs it. Only the app's version is sent (in the User-Agent).
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "MarcoTrombetta/SpiffoCON";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    static readonly string DownloadFolder = Path.Combine(Path.GetTempPath(), "SpiffoCON-update");

    static HttpClient NewClient(Version current, TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpiffoCON", current.ToString()));
        return http;
    }

    /// <summary>The latest release, or null when GitHub can't be reached (offline, rate limit...).</summary>
    public static async Task<ReleaseInfo?> LatestAsync(Version current, CancellationToken ct = default)
    {
        using var http = NewClient(current, TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Parse(json.RootElement);
    }

    public static ReleaseInfo? Parse(JsonElement release)
    {
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString()?.TrimStart('v', 'V') : null;
        if (!Version.TryParse(tag, out var version))
            return null;
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                // GitHub gives every asset a digest ("sha256:...")
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                assets.Add(new ReleaseAsset(
                    a.GetProperty("name").GetString() ?? "",
                    a.GetProperty("browser_download_url").GetString() ?? "",
                    a.GetProperty("size").GetInt64(),
                    digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? digest[7..] : null));
            }
        }
        DateTime? published = release.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetDateTime().ToUniversalTime()
            : null;
        return new ReleaseInfo(version, notes, assets, published);
    }

    /// <summary>
    /// The installer this copy updates itself with: Full for the self-contained build, Light otherwise; none
    /// for a copy the installer didn't put there (the releases page is opened instead).
    /// </summary>
    public static ReleaseAsset? InstallerFor(ReleaseInfo release, InstallKind install, bool selfContained)
    {
        if (install is not (InstallKind.AllUsers or InstallKind.CurrentUser))
            return null;
        var name = $"SpiffoCON-Setup-{release.Version}-{(selfContained ? "Full" : "Light")}.exe";
        return release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"SpiffoCON-Setup-0.11.1-Full.exe" → 0.11.1.</summary>
    public static Version? VersionInName(string fileName)
    {
        var parts = fileName.Split('-');
        return parts.Length > 3 && parts[0] == "SpiffoCON" && parts[1] == "Setup" && Version.TryParse(parts[2], out var v) ? v : null;
    }

    /// <summary>The release notes without the downloads list (the app picks the file itself), as plain text.</summary>
    public static string NotesForDisplay(string notes)
    {
        int downloads = notes.IndexOf("## Downloads", StringComparison.OrdinalIgnoreCase);
        var text = (downloads >= 0 ? notes[..downloads] : notes).Replace("\r\n", "\n").Trim();
        // headings and bold read better without their Markdown signs
        return string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("*", "").Replace("`", "")));
    }

    /// <summary>
    /// Removes the downloaded installers of versions already installed (this one or older); a newer one is kept:
    /// its update did not happen, and the user was told it can be run by hand.
    /// </summary>
    public static void CleanUpDownloads(Version current)
    {
        try
        {
            if (!Directory.Exists(DownloadFolder))
                return;
            foreach (var file in Directory.GetFiles(DownloadFolder))
            {
                var version = VersionInName(Path.GetFileName(file));
                bool done = version is not null ? version <= current : File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7);
                if (done)
                {
                    try { File.Delete(file); }
                    catch (IOException) { } // still running: next time
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // only a temp folder
        }
    }

    /// <summary>Downloads the installer to the temp folder, checking its size and SHA-256; returns its path.</summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, Version current, IProgress<long>? progress, CancellationToken ct, string? folder = null)
    {
        folder ??= DownloadFolder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileName(asset.Name));
        var partial = path + ".part";
        try
        {
            using var http = NewClient(current, TimeSpan.FromMinutes(30));
            using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var sha = SHA256.Create();
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long total = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    total += n;
                    progress?.Report(total);
                }
                sha.TransformFinalBlock([], 0, 0);
                if (total != asset.Size)
                    throw new IOException($"The download was incomplete ({total} of {asset.Size} bytes). Please try again.");
            }
            if (asset.Sha256 is not null && !string.Equals(Convert.ToHexString(sha.Hash!), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The downloaded file is not the one published on GitHub (checksum mismatch). Please try again.");
            File.Move(partial, path, overwrite: true);
            return path;
        }
        finally
        {
            if (File.Exists(partial))
                File.Delete(partial);
        }
    }

    /// <summary>
    /// The event SpiffoCON waits on while its installer runs: the installer sets it once it really installs
    /// (past the administrator prompt), and SpiffoCON then closes by itself. SpiffoCON.iss builds the same name.
    /// </summary>
    public static string ReadyEventName(int process) => $"SpiffoCON-update-{process}";

    /// <summary>
    /// Runs the installer quietly, for the same users as the installed copy, and asks it to open SpiffoCON again
    /// when done. SpiffoCON stays open meanwhile: the installer asks it to close (<see cref="ReadyEventName"/>) only
    /// once it really installs, so if the administrator prompt is declined, SpiffoCON is still there to say so.
    /// </summary>
    /// <param name="waitForProcess">This process: the installer waits for it to be gone before it replaces any file.</param>
    public static Process StartInstaller(string installer, InstallKind install, int waitForProcess) =>
        Process.Start(new ProcessStartInfo(installer)
        {
            UseShellExecute = true,
            Arguments = "/SILENT /SP- /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /UPDATE=1 "
                + $"/WAITPID={waitForProcess} " + (install == InstallKind.AllUsers ? "/ALLUSERS" : "/CURRENTUSER"),
        }) ?? throw new InvalidOperationException("The installer did not start.");

    public static void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no browser
        }
    }
}

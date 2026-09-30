using System.Text.Json;
using SpiffoCON.Core.Updates;

namespace SpiffoCON.Core.Tests;

public sealed class UpdateCheckTests
{
    // the fields the GitHub API gives for a release (trimmed)
    const string Release = """
        {
          "tag_name": "v0.12.0",
          "published_at": "2026-10-01T10:00:00Z",
          "body": "## New\n\n- **Weather**: fog and `wind` (*beta*).\n\n## Downloads\n\n- **SpiffoCON-Setup-0.12.0-Full.exe**: includes the .NET runtime.",
          "assets": [
            { "name": "SpiffoCON-Setup-0.12.0-Full.exe", "browser_download_url": "https://example.net/full.exe", "size": 54000000,
              "digest": "sha256:ab12" },
            { "name": "SpiffoCON-Setup-0.12.0-Light.exe", "browser_download_url": "https://example.net/light.exe", "size": 6000000 }
          ]
        }
        """;

    static ReleaseInfo Parse() => UpdateCheck.Parse(JsonDocument.Parse(Release).RootElement)!;

    [Fact]
    public void A_release_is_read_with_its_installers_and_checksums()
    {
        var release = Parse();
        Assert.Equal(new Version(0, 12, 0), release.Version);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), release.Published);
        Assert.Equal(("ab12", (string?)null), (release.Assets[0].Sha256, release.Assets[1].Sha256));
        Assert.Null(UpdateCheck.Parse(JsonDocument.Parse("""{ "tag_name": "nightly" }""").RootElement));
    }

    [Fact]
    public void Each_copy_is_updated_by_its_own_edition()
    {
        var release = Parse();
        Assert.Equal("SpiffoCON-Setup-0.12.0-Full.exe", UpdateCheck.InstallerFor(release, InstallKind.CurrentUser, selfContained: true)?.Name);
        Assert.Equal("SpiffoCON-Setup-0.12.0-Light.exe", UpdateCheck.InstallerFor(release, InstallKind.AllUsers, selfContained: false)?.Name);
        // a build or a copied folder: the releases page instead
        Assert.Null(UpdateCheck.InstallerFor(release, InstallKind.Other, selfContained: true));
    }

    [Fact]
    public void The_notes_are_shown_without_the_downloads_and_markdown()
    {
        Assert.Equal($"New{Environment.NewLine}{Environment.NewLine}- Weather: fog and wind (beta).", UpdateCheck.NotesForDisplay(Parse().Notes));
    }

    [Fact]
    public void Downloaded_installers_are_recognised_by_their_version()
    {
        Assert.Equal(new Version(0, 11, 1), UpdateCheck.VersionInName("SpiffoCON-Setup-0.11.1-Full.exe"));
        Assert.Equal(new Version(0, 12, 0), UpdateCheck.VersionInName("SpiffoCON-Setup-0.12.0-Light.exe"));
        Assert.Null(UpdateCheck.VersionInName("SpiffoCON-Setup-0.12.0-Full.exe.part".Replace("0.12.0", "x")));
        Assert.Null(UpdateCheck.VersionInName("other.exe"));
    }
}

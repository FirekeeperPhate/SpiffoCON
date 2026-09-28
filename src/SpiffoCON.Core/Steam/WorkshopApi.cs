using System.Text.Json;

namespace SpiffoCON.Core.Steam;

public sealed record WorkshopItemInfo(string Id, string Title, DateTimeOffset Updated, long Size, bool Exists);

/// <summary>Public Steam Web API calls that need no key.</summary>
public sealed class WorkshopApi(HttpClient http)
{
    const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    public async Task<IReadOnlyDictionary<string, WorkshopItemInfo>> GetDetailsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<string, WorkshopItemInfo>();
        foreach (var batch in ids.Distinct().Chunk(100))
        {
            var form = new List<KeyValuePair<string, string>> { new("itemcount", batch.Length.ToString()) };
            for (int i = 0; i < batch.Length; i++)
                form.Add(new($"publishedfileids[{i}]", batch[i]));

            using var response = await http.PostAsync(DetailsUrl, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            foreach (var item in doc.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
            {
                var id = item.GetProperty("publishedfileid").GetString() ?? "";
                bool ok = item.TryGetProperty("result", out var r) && r.GetInt32() == 1;
                result[id] = new WorkshopItemInfo(
                    id,
                    ok && item.TryGetProperty("title", out var t) ? t.GetString() ?? id : id,
                    ok && item.TryGetProperty("time_updated", out var u) ? DateTimeOffset.FromUnixTimeSeconds(u.GetInt64()) : default,
                    ok && item.TryGetProperty("file_size", out var s) && long.TryParse(s.ToString(), out var size) ? size : 0,
                    ok);
            }
        }
        return result;
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// 获取 Last.fm <c>track.getInfo</c> 的 <c>wiki</c>（曲目解说）。只需免费 API 密钥，
/// 没有认证流程。常能补上 Genius 里没有 About 的曲目。
///
/// 正文末尾总会有 HTML 形式的 "Read more on Last.fm" 链接，需要截掉。
/// </summary>
public sealed partial class LastFmSource : ISongMeaningSource
{
    private const string Endpoint = "https://ws.audioscrobbler.com/2.0/";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;

    public LastFmSource(string apiKey, HttpClient? http = null, int timeoutMs = 6000)
    {
        _apiKey = apiKey.Trim();
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    public string Name => "Last.fm";

    public async Task<MeaningSource?> FetchAsync(string title, string artist, CancellationToken ct = default)
    {
        if (_apiKey.Length == 0 || string.IsNullOrWhiteSpace(artist)) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            foreach (var (t, a) in Variants(title, artist))
            {
                var url = $"{Endpoint}?method=track.getinfo&format=json"
                        + $"&api_key={Uri.EscapeDataString(_apiKey)}"
                        + $"&artist={Uri.EscapeDataString(a)}&track={Uri.EscapeDataString(t)}"
                        + "&autocorrect=1";

                // Last.fm 连错误也放在 HTTP 200 里返回，所以必须看正文。
                var body = await _http.GetFromJsonAsync<LastFmEnvelope>(url, Json, cts.Token).ConfigureAwait(false);
                var wiki = body?.Track?.Wiki;
                var text = Clean(wiki?.Content) ?? Clean(wiki?.Summary);
                if (text is { Length: >= 40 })
                    return new MeaningSource(Name, body!.Track!.Url, text);
            }
            return null;
        }
        catch (Exception)
        {
            return null; // 静默降级
        }
    }

    private static IEnumerable<(string Title, string Artist)> Variants(string title, string artist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add($"{title}|{artist}")) yield return (title, artist);

        foreach (var variant in SearchTermCleaner.Variants(new SearchTerm(title, artist)))
        {
            if (variant.IsKeyword) continue;
            var t = variant.Title ?? title;
            var a = variant.Artist ?? artist;
            if (seen.Add($"{t}|{a}")) yield return (t, a);
        }
    }

    /// <summary>清除 HTML 标签与尾巴标记（"Read more on Last.fm"）。</summary>
    private static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = TagRegex().Replace(raw, " ");
        var marker = text.IndexOf("Read more on Last.fm", StringComparison.OrdinalIgnoreCase);
        if (marker > 0) text = text[..marker];
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text.Length == 0 ? null : System.Net.WebUtility.HtmlDecode(text);
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // ---- 响应模型（仅保留需要的字段）----

    private sealed record LastFmEnvelope(LastFmTrack? Track);
    private sealed record LastFmTrack(string? Url, LastFmWiki? Wiki);
    private sealed record LastFmWiki(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("content")] string? Content);
}

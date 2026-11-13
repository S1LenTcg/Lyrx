using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// Last.fm <c>track.getInfo</c>의 <c>wiki</c>(곡 해설)를 가져온다. 무료 API 키만 있으면 되고
/// 인증 플로우가 없다. Genius에 About이 없는 곡을 자주 메워 준다.
///
/// 본문 끝에는 항상 "Read more on Last.fm" 링크가 HTML로 붙어 오므로 잘라 낸다.
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

                // Last.fm은 오류도 HTTP 200에 담아 보내므로 본문을 봐야 한다.
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
            return null; // 조용한 강등
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

    /// <summary>HTML 태그와 꼬리표("Read more on Last.fm")를 걷어 낸다.</summary>
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

    // ---- 응답 모델(필요한 필드만) ----

    private sealed record LastFmEnvelope(LastFmTrack? Track);
    private sealed record LastFmTrack(string? Url, LastFmWiki? Wiki);
    private sealed record LastFmWiki(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("content")] string? Content);
}

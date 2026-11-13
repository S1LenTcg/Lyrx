using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Musebase.Core.Search;

/// <summary>
/// NetEase 클라우드 뮤직. LyricsKit의 NetEase.swift 포팅.
/// 검색은 공개 API(2-pass 쿠키), 가사는 EAPI(AES-ECB) 사용.
/// yrc/klyric(글자 단위) 형식은 P1 — 현재는 lrc + tlyric 번역 병합만.
/// </summary>
public sealed partial class NetEaseProvider : LyricsProviderBase<NetEaseProvider.Song>
{
    public override string ServiceName => "NetEase";

    private const string SearchUrl = "http://music.163.com/api/search/pc";
    private const string LyricsEapiUrl = "https://interface3.music.163.com/eapi/song/lyric/v1";
    private const string SearchUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.4 Safari/605.1.15";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly NetEaseEapiClient _eapi;

    public NetEaseProvider(HttpClient? http = null)
    {
        _http = http ?? LyricsHttp.Client;
        _eapi = new NetEaseEapiClient(_http);
    }

    protected override async Task<IReadOnlyList<Song>> SearchAsync(LyricsSearchRequest request, CancellationToken ct)
    {
        var url = $"{SearchUrl}?s={Uri.EscapeDataString(request.Term.ToString())}&offset=0&limit=10&type=1";

        HttpRequestMessage BuildRequest(string? cookie)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("Referer", "http://music.163.com/");
            req.Headers.TryAddWithoutValidation("User-Agent", SearchUserAgent);
            if (cookie is not null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
            return req;
        }

        // 1차 요청에서 Set-Cookie 추출 후 2차 요청 (원본 로직 유지)
        string? cookie = null;
        using (var first = await _http.SendAsync(BuildRequest(null), ct).ConfigureAwait(false))
        {
            if (first.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                var raw = setCookies.FirstOrDefault();
                var semi = raw?.IndexOf(';') ?? -1;
                if (raw is not null && semi > 0) cookie = raw[..semi];
            }
        }

        using var response = await _http.SendAsync(BuildRequest(cookie), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var result = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
        return result?.Result?.Songs ?? [];
    }

    protected override async Task<Lyrics?> FetchAsync(Song token, CancellationToken ct)
    {
        var payload = new Dictionary<string, string>
        {
            ["id"] = token.Id.ToString(CultureInfo.InvariantCulture),
            ["cp"] = "false",
            ["lv"] = "0",
            ["kv"] = "0",
            ["tv"] = "0",
            ["rv"] = "0",
            ["yv"] = "0",
            ["ytv"] = "0",
            ["yrv"] = "0",
            ["csrf_token"] = "",
        };

        var raw = await _eapi.PostAsync(LyricsEapiUrl, payload, ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<LyricsResponse>(raw, JsonOptions);
        if (response is null) return null;

        // 글자 단위(yrc 신형 → klyric 구형) 우선, 없으면 라인 단위 lrc
        var lyrics = ParseWordLevel(response) ?? ParseFixed(response.Lrc?.Lyric);
        if (lyrics is null) return null;

        var translation = ParseFixed(response.Tlyric?.Lyric);
        if (translation is not null) lyrics.MergeTranslation(translation);

        lyrics.IdTags[Lyrics.TagTitle] = token.Name;
        if (token.Artists is [{ Name: { } artistName }, ..])
            lyrics.IdTags[Lyrics.TagArtist] = artistName;
        if (token.Album?.Name is { } albumName)
            lyrics.IdTags[Lyrics.TagAlbum] = albumName;
        if (response.LyricUser?.Nickname is { } nickname)
            lyrics.IdTags[Lyrics.TagLrcBy] = nickname;
        lyrics.IdTags[Lyrics.TagLength] = (token.Duration / 1000.0).ToString("0.##", CultureInfo.InvariantCulture);
        if (token.Album?.PicUrl is { } pic && Uri.TryCreate(pic, UriKind.Absolute, out var picUri))
            lyrics.Metadata.ArtworkUrl = picUri;
        lyrics.Metadata.ServiceToken = token.Id.ToString(CultureInfo.InvariantCulture);
        return lyrics;
    }

    /// <summary>NetEase 특유의 [mm:ss:xx] 타임태그를 [mm:ss.xx]로 교정 후 파싱</summary>
    private static Lyrics? ParseFixed(string? lrc) =>
        string.IsNullOrEmpty(lrc) ? null : Lyrics.Parse(TimeTagFixer().Replace(lrc, "$1.$2"));

    /// <summary>글자 단위 가사(yrc/klyric) 파싱. 유효 라인이 없으면 null.</summary>
    private static Lyrics? ParseWordLevel(LyricsResponse response)
    {
        if (!string.IsNullOrEmpty(response.Yrc?.Lyric) &&
            NetEaseLyricParser.ParseYrc(response.Yrc.Lyric) is { } yrc)
            return yrc;

        if (!string.IsNullOrEmpty(response.Klyric?.Lyric) &&
            NetEaseLyricParser.ParseKLyric(response.Klyric.Lyric) is { } klyric)
            return klyric;

        return null;
    }

    [GeneratedRegex(@"(\[\d+:\d+):(\d+\])")]
    private static partial Regex TimeTagFixer();

    // ---- 응답 모델 ----

    internal sealed record SearchResponse([property: JsonPropertyName("result")] SearchResult? Result);

    internal sealed record SearchResult([property: JsonPropertyName("songs")] List<Song>? Songs);

    public sealed record Song(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("duration")] long Duration,
        [property: JsonPropertyName("artists")] List<Artist>? Artists,
        [property: JsonPropertyName("album")] Album? Album);

    public sealed record Artist([property: JsonPropertyName("name")] string? Name);

    public sealed record Album(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("picUrl")] string? PicUrl);

    internal sealed record LyricsResponse(
        [property: JsonPropertyName("lrc")] LyricBlock? Lrc,
        [property: JsonPropertyName("klyric")] LyricBlock? Klyric,
        [property: JsonPropertyName("tlyric")] LyricBlock? Tlyric,
        [property: JsonPropertyName("yrc")] LyricBlock? Yrc,
        [property: JsonPropertyName("lyricUser")] LyricUser? LyricUser);

    internal sealed record LyricBlock([property: JsonPropertyName("lyric")] string? Lyric);

    internal sealed record LyricUser([property: JsonPropertyName("nickname")] string? Nickname);
}

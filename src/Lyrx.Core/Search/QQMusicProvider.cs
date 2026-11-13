using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Musebase.Core.Search;

/// <summary>
/// QQ 音乐(QQMusic) 제공자. LyricsKit의 QQMusic.swift 포팅.
/// 검색은 smartbox(GET) + musicu(POST) 두 엔드포인트를 병합하고,
/// lyric_download.fcg 응답 XML의 &lt;content&gt;/&lt;contentts&gt;를 QRC(3중 DES) 복호해 파싱한다.
/// 네트워크·XML 스키마는 오프라인 검증이 불가하여 방어적으로 처리한다(실패 시 후보 건너뜀).
/// </summary>
public sealed partial class QQMusicProvider : LyricsProviderBase<QQMusicProvider.SongToken>
{
    public override string ServiceName => "QQMusic";

    private const string SmartboxUrl = "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg";
    private const string MusicuUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
    private const string LyricsUrl = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public QQMusicProvider(HttpClient? http = null) => _http = http ?? LyricsHttp.Client;

    protected override async Task<IReadOnlyList<SongToken>> SearchAsync(LyricsSearchRequest request, CancellationToken ct)
    {
        var tokens = new List<SongToken>();
        foreach (var result in await Task.WhenAll(SearchSmartboxAsync(request, ct), SearchMusicuAsync(request, ct)).ConfigureAwait(false))
            tokens.AddRange(result);
        return tokens;
    }

    private async Task<IReadOnlyList<SongToken>> SearchSmartboxAsync(LyricsSearchRequest request, CancellationToken ct)
    {
        try
        {
            var url = $"{SmartboxUrl}?key={Uri.EscapeDataString(request.Term.ToString())}";
            var resp = await _http.GetFromJsonAsync<SmartboxResponse>(url, JsonOptions, ct).ConfigureAwait(false);
            return resp?.Data?.Song?.ItemList?
                .Select(i => new SongToken(i.Id, i.Mid, i.Name, [i.Singer]))
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<SongToken>> SearchMusicuAsync(LyricsSearchRequest request, CancellationToken ct)
    {
        try
        {
            var body = new
            {
                req_1 = new
                {
                    method = "DoSearchForQQMusicDesktop",
                    module = "music.search.SearchCgiService",
                    param = new { num_per_page = 20, page_num = 1, query = request.Term.ToString(), search_type = 0 },
                },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, MusicuUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var parsed = await JsonSerializer.DeserializeAsync<MusicuResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
            var list = parsed?.Req1?.Data?.Body?.Song?.List;
            if (parsed?.Req1?.Code != 0 || list is null) return [];
            return list
                .Select(i => new SongToken(
                    i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    i.Mid, i.Name, i.Singer?.Select(s => s.Name).ToList() ?? []))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    protected override async Task<Lyrics?> FetchAsync(SongToken token, CancellationToken ct)
    {
        var form = $"musicid={Uri.EscapeDataString(token.Id)}&version=15&miniversion=82&lrctype=4";
        using var req = new HttpRequestMessage(HttpMethod.Post, LyricsUrl)
        {
            Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        req.Headers.TryAddWithoutValidation("Referer", "https://c.y.qq.com/");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var dataString = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        dataString = dataString.Replace("<!--", "").Replace("-->", "");

        var origRaw = ExtractElement(dataString, "content");
        if (origRaw is null) return null;
        var orig = DecodeSingleLyricText(origRaw);
        if (orig is null) return null;

        var lyrics = QrcParser.Parse(orig) ?? Lyrics.Parse(orig);
        if (lyrics is null) return null;

        var transRaw = ExtractElement(dataString, "contentts");
        if (transRaw is not null && DecodeSingleLyricText(transRaw) is { } transText)
        {
            var trans = QrcParser.Parse(transText) ?? Lyrics.Parse(transText);
            if (trans is not null) lyrics.MergeTranslation(trans);
        }

        lyrics.IdTags[Lyrics.TagTitle] = token.Name;
        lyrics.IdTags[Lyrics.TagArtist] = string.Join(",", token.Singers);
        lyrics.Metadata.ServiceToken = token.Mid;
        return lyrics;
    }

    // ---- QRC/XML 복호 (QQMusicXMLDecoder.swift 상당, 경량화) ----

    /// <summary>
    /// &lt;name ...속성...&gt;내용&lt;/name&gt; 요소의 내부 텍스트 추출(첫 매치).
    /// 실제 QQ 응답은 `&lt;content type="file" ...&gt;&lt;![CDATA[HEX]]&gt;` 형태라
    /// 속성을 허용하고 CDATA 래퍼를 벗긴다.
    /// </summary>
    private static string? ExtractElement(string xml, string name)
    {
        var m = Regex.Match(xml, $@"<{name}\b[^>]*>(.*?)</{name}>", RegexOptions.Singleline);
        if (!m.Success) return null;

        var inner = m.Groups[1].Value;
        var cdata = Regex.Match(inner, @"<!\[CDATA\[(.*?)\]\]>", RegexOptions.Singleline);
        return cdata.Success ? cdata.Groups[1].Value : inner;
    }

    /// <summary>
    /// hex(QRC)면 3중 DES 복호, 아니면 원문. 중첩 XML(LyricContent 속성)이면 실제 가사만 추출한다.
    /// </summary>
    internal static string? DecodeSingleLyricText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;

        var compact = WhitespaceRegex().Replace(trimmed, "");
        string decoded;
        if (IsStrictHex(compact))
        {
            var qrc = QQMusicQrcDecrypter.Decode(compact);
            if (qrc is null) return null;
            decoded = qrc;
        }
        else
        {
            decoded = trimmed;
        }

        if (!decoded.Contains("<?xml", StringComparison.Ordinal)) return decoded;

        var attr = Regex.Match(decoded, "LyricContent=\"([^\"]*)\"", RegexOptions.Singleline);
        if (attr.Success && attr.Groups[1].Value.Length > 0)
            return NormalizeQqQrc(LyricFormat(attr.Groups[1].Value));

        return decoded;
    }

    private static bool IsStrictHex(string s) =>
        s.Length > 0 && s.Length % 2 == 0 && s.All(Uri.IsHexDigit);

    private static string LyricFormat(string lyric) => lyric
        .Replace("&#10;", "\n").Replace("&#13;", "\r").Replace("&#32;", " ").Replace("&#39;", "'")
        .Replace("&#40;", "(").Replace("&#41;", ")").Replace("&#45;", "-").Replace("&#46;", ".")
        .Replace("&#58;", ":").Replace("&#64;", "@").Replace("&#95;", "_").Replace("&#124;", "|");

    private static string NormalizeQqQrc(string content) =>
        Regex.Replace(
            Regex.Replace(content, @"\s+(?=\[\d+,\d+\])", "\n"),
            @"\]\s+\[", "]\n[").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // ---- 검색 토큰 & 응답 모델 ----

    public sealed record SongToken(string Id, string Mid, string Name, IReadOnlyList<string> Singers);

    internal sealed record SmartboxResponse([property: JsonPropertyName("data")] SmartboxData? Data);
    internal sealed record SmartboxData([property: JsonPropertyName("song")] SmartboxSong? Song);
    internal sealed record SmartboxSong([property: JsonPropertyName("itemlist")] List<SmartboxItem>? ItemList);
    internal sealed record SmartboxItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("mid")] string Mid,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("singer")] string Singer);

    internal sealed record MusicuResponse([property: JsonPropertyName("req_1")] MusicuReq? Req1);
    internal sealed record MusicuReq(
        [property: JsonPropertyName("code")] int Code,
        [property: JsonPropertyName("data")] MusicuData? Data);
    internal sealed record MusicuData([property: JsonPropertyName("body")] MusicuBody? Body);
    internal sealed record MusicuBody([property: JsonPropertyName("song")] MusicuSong? Song);
    internal sealed record MusicuSong([property: JsonPropertyName("list")] List<MusicuItem>? List);
    internal sealed record MusicuItem(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("mid")] string Mid,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("singer")] List<MusicuSinger>? Singer);
    internal sealed record MusicuSinger([property: JsonPropertyName("name")] string Name);
}

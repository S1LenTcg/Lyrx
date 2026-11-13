using System.Text.Json;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>Musixmatch가 알려 준 곡 하나. <see cref="ShareUrl"/>이 그 곡의 공식 페이지 주소다.</summary>
public sealed record MusixmatchTrack(long TrackId, string? ShareUrl, string? Name, string? Artist);

/// <summary>
/// Musixmatch 공식 API(<c>api.musixmatch.com/ws/1.1</c>)로 곡을 찾아 **정확한 곡 페이지 주소**를 얻는다.
///
/// <b>주소를 규칙으로 만들면 안 되는 이유</b>가 실측으로 확인됐다 — <c>/lyrics/Pearl-Jam/Even-Flow</c>는
/// 오류 없이 200을 주면서 조용히 <c>/lyrics/Pearl-Jam/Alive</c>(다른 곡!)로 넘어간다.
/// 사용자를 엉뚱한 곡으로 보내는 실패라 추측은 금지이고, 검색 결과 페이지를 서버가 긁는 길도
/// 익명 요청이 로그인 페이지로 리다이렉트되어 막혀 있다. 남은 정당한 길이 이 API다.
///
/// 키는 <see href="https://developer.musixmatch.com"/>에서 발급한다. 없으면 조용히 꺼진다.
/// </summary>
public sealed class MusixmatchApi
{
    private const string BaseUrl = "https://api.musixmatch.com/ws/1.1";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;

    public MusixmatchApi(string apiKey, HttpClient? http = null, int timeoutMs = 6000)
    {
        _apiKey = (apiKey ?? "").Trim();
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    public bool IsConfigured => _apiKey.Length > 0;

    public async Task<MusixmatchTrack?> FindAsync(string title, string artist, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            foreach (var (t, a) in Terms(title, artist))
            {
                var url = $"{BaseUrl}/track.search"
                        + $"?q_track={Uri.EscapeDataString(t)}"
                        + $"&q_artist={Uri.EscapeDataString(a)}"
                        + "&page_size=5&s_track_rating=desc&apikey=" + Uri.EscapeDataString(_apiKey);

                var json = await _http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
                if (Pick(json, title, artist) is { } track) return track;
            }
            return null;
        }
        catch (Exception)
        {
            return null; // 소스·링크 모두 부가 기능 — 실패해도 가사에 영향이 없어야 한다
        }
    }

    /// <summary>
    /// 응답에서 이 곡을 고른다(순수 함수 — 테스트 대상).
    ///
    /// <b>HTTP 200이어도 실패일 수 있다</b> — Musixmatch는 성공/실패를 본문의
    /// <c>message.header.status_code</c>에 싣는다(키 오류 401, 플랜 초과 402 …).
    /// 결과가 없을 때 <c>body</c>가 객체가 아니라 빈 배열로 오는 경우도 있어
    /// 레코드 역직렬화 대신 <see cref="JsonDocument"/>로 방어적으로 읽는다.
    /// </summary>
    internal static MusixmatchTrack? Pick(string json, string title, string artist)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("message", out var message)) return null;

        if (message.TryGetProperty("header", out var header)
            && header.TryGetProperty("status_code", out var status)
            && status.TryGetInt32(out var code) && code != 200) return null;

        if (!message.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object) return null;
        if (!body.TryGetProperty("track_list", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        foreach (var wrapper in list.EnumerateArray())
        {
            if (!wrapper.TryGetProperty("track", out var track)) continue;

            var name = Text(track, "track_name");
            var by = Text(track, "artist_name");

            // 검색 API는 무엇을 넣든 뭔가를 돌려준다 — 받아들이기 전에 확인한다.
            if (!MeaningMatch.IsSameSong(name, by, title, artist)) continue;

            var id = track.TryGetProperty("track_id", out var idEl) && idEl.TryGetInt64(out var v) ? v : 0;
            return new MusixmatchTrack(id, Text(track, "track_share_url"), name, by);
        }
        return null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>원본 표기 → 정제 표기 순으로 시도한다. 아티스트는 대표 이름 하나만 쓴다.</summary>
    private static IEnumerable<(string Title, string Artist)> Terms(string title, string artist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        (string, string) Make(string t, string a) => (t.Trim(), ArtistNames.Primary(a));

        var first = Make(title, artist);
        if (seen.Add($"{first.Item1}|{first.Item2}")) yield return first;

        foreach (var variant in SearchTermCleaner.Variants(new SearchTerm(title, artist)))
        {
            if (variant.IsKeyword) continue;
            var next = Make(variant.Title ?? title, variant.Artist ?? artist);
            if (seen.Add($"{next.Item1}|{next.Item2}")) yield return next;
        }
    }
}

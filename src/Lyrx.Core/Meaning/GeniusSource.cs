using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// Genius 공식 API로 곡의 "About"(<c>description</c>)을 가져온다.
///
/// 무료 Client Access Token만 있으면 되고 OAuth 사용자 플로우가 필요 없다
/// (<see href="https://genius.com/api-clients"/>에서 발급). 두 번 호출한다:
/// <c>GET /search?q=</c>로 곡 id를 찾고 → <c>GET /songs/{id}?text_format=plain</c>에서
/// <c>description</c>과 정확한 곡 페이지 <c>url</c>을 읽는다.
///
/// **가사 본문은 이 API로 오지 않는다**(그건 스크래핑 영역) — 우리는 이미 가지고 있으므로 상관없다.
/// </summary>
public sealed class GeniusSource : ISongMeaningSource
{
    private const string BaseUrl = "https://api.genius.com";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly TimeSpan _timeout;

    /// <param name="timeoutMs">
    /// **두 번의 순차 호출 전체**에 대한 예산이라 넉넉해야 한다. 실측에서 유명 곡일수록
    /// 설명이 길어 느렸다 — <c>Lady Gaga / Shallow</c>는 검색 0.96초 + 상세 2.0초로 2.95초였고,
    /// 예전 기본값 2.5초에서는 **정확히 그런 곡들만 조용히 잘려 나갔다**(자료가 가장 좋은 곡들이다).
    /// 가사 검색과 달리 여기서는 사람이 버튼을 누르고 기다리므로 지연보다 누락이 훨씬 나쁘다.
    /// </param>
    public GeniusSource(string token, HttpClient? http = null, int timeoutMs = 8000)
    {
        _token = token.Trim();
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    public string Name => "Genius";

    public async Task<MeaningSource?> FetchAsync(string title, string artist, CancellationToken ct = default)
    {
        if (_token.Length == 0) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var hit = await SearchAsync(title, artist, cts.Token).ConfigureAwait(false);
            if (hit is null) return null;

            var song = await GetAsync<GeniusSongEnvelope>(
                $"/songs/{hit.Id}?text_format=plain", cts.Token).ConfigureAwait(false);
            var description = song?.Response?.Song?.Description?.Plain;
            var url = song?.Response?.Song?.Url ?? hit.Url;

            // 설명이 비어 있는 곡이 많다(대부분의 곡에 About이 없다). 그때는 소스가 없는 것으로 본다.
            if (string.IsNullOrWhiteSpace(description) || description!.Trim().Length < 40)
                return null;

            return new MeaningSource(Name, url, description.Trim());
        }
        catch (Exception)
        {
            return null; // 조용한 강등 — 다른 소스가 채운다
        }
    }

    /// <summary>
    /// 원본 표기와 정제 표기를 순서대로 시도한다. 스트리밍 메타데이터의 잡음
    /// (피처링·리마스터 표기, "• 스마트셔플 추천" 같은 꼬리표)은 Genius 검색을 그냥 실패시킨다.
    /// </summary>
    private async Task<GeniusHit?> SearchAsync(string title, string artist, CancellationToken ct)
    {
        foreach (var term in Terms(title, artist))
        {
            var url = "/search?q=" + Uri.EscapeDataString(term);
            var found = await GetAsync<GeniusSearchEnvelope>(url, ct).ConfigureAwait(false);
            if (found?.Response?.Hits is not { Length: > 0 } hits) continue;

            foreach (var wrapper in hits)
            {
                if (!string.Equals(wrapper.Type, "song", StringComparison.OrdinalIgnoreCase)) continue;
                if (wrapper.Result is not { Id: > 0 } song) continue;
                if (!Matches(song.Title, song.Artists, title, artist)) continue;
                return song;
            }
        }
        return null;
    }

    /// <summary>
    /// 이 검색 결과가 정말 그 곡인지 확인한다. 판정 기준은 검색 기반 소스가 모두 공유한다
    /// (<see cref="MeaningMatch.IsSameSong"/> — 그쪽에 이유를 적어 뒀다).
    /// </summary>
    internal static bool Matches(string? hitTitle, string? hitArtists, string title, string artist) =>
        MeaningMatch.IsSameSong(hitTitle, hitArtists, title, artist);

    private static IEnumerable<string> Terms(string title, string artist)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Compose(string t, string a) => string.IsNullOrWhiteSpace(a) ? t.Trim() : $"{a.Trim()} {t.Trim()}";

        if (seen.Add(Compose(title, artist))) yield return Compose(title, artist);

        foreach (var variant in SearchTermCleaner.Variants(new SearchTerm(title, artist)))
        {
            if (variant.IsKeyword) continue;
            var term = Compose(variant.Title ?? title, variant.Artist ?? artist);
            if (seen.Add(term)) yield return term;
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return default;
        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
    }

    // ---- 응답 모델(필요한 필드만) ----

    private sealed record GeniusSearchEnvelope(GeniusSearchResponse? Response);
    private sealed record GeniusSearchResponse(GeniusHitWrapper[]? Hits);
    private sealed record GeniusHitWrapper(string? Type, GeniusHit? Result);
    private sealed record GeniusHit(
        long Id,
        string? Url,
        string? Title,
        [property: JsonPropertyName("artist_names")] string? Artists);

    private sealed record GeniusSongEnvelope(GeniusSongResponse? Response);
    private sealed record GeniusSongResponse(GeniusSong? Song);
    private sealed record GeniusSong(string? Url, GeniusDescription? Description);

    /// <summary><c>text_format=plain</c>이면 설명이 <c>{"plain": "..."}</c>로 온다.</summary>
    private sealed record GeniusDescription([property: JsonPropertyName("plain")] string? Plain);
}

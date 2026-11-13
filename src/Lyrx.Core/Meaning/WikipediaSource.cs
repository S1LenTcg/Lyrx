using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// Wikipedia(MediaWiki API)에서 곡 문서의 도입부를 가져온다. **키가 필요 없다.**
/// 유명 곡은 "Composition"·"Lyrics and interpretation" 같은 절이 통째로 있어 가장 밀도가 높다.
///
/// 검색은 <c>list=search</c>로 하되 **"(song)" 문서를 우선**한다 — 같은 제목의 앨범·영화
/// 문서가 먼저 잡히는 일이 흔하다. 본문은 <c>prop=extracts&amp;exintro&amp;explaintext</c>로 받는다.
///
/// 문서 본문은 <b>CC BY-SA</b>다 — 출처와 라이선스를 화면에 함께 표기해야 한다(호출자 책임).
/// </summary>
public sealed partial class WikipediaSource : ISongMeaningSource
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly TimeSpan _timeout;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <param name="language">위키 언어 코드. 기본 영어 — 곡 해설은 영어판이 압도적으로 두껍다.</param>
    /// <param name="timeoutMs">검색 + 본문 두 호출 전체의 예산(<see cref="GeniusSource"/> 참고).</param>
    public WikipediaSource(string language = "en", HttpClient? http = null, int timeoutMs = 6000)
    {
        _endpoint = $"https://{language}.wikipedia.org/w/api.php";
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    public string Name => "Wikipedia";

    public async Task<MeaningSource?> FetchAsync(string title, string artist, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var pageTitle = await FindPageAsync(title, artist, cts.Token).ConfigureAwait(false);
            if (pageTitle is null) return null;

            var url = $"{_endpoint}?action=query&format=json&formatversion=2&redirects=1"
                    + "&prop=extracts&exintro=1&explaintext=1"
                    + $"&titles={Uri.EscapeDataString(pageTitle)}";
            var body = await _http.GetFromJsonAsync<QueryEnvelope>(url, Json, cts.Token).ConfigureAwait(false);
            var extract = body?.Query?.Pages?.FirstOrDefault()?.Extract;
            if (string.IsNullOrWhiteSpace(extract) || extract!.Trim().Length < 60) return null;

            var text = WhitespaceRegex().Replace(extract, " ").Trim();
            var pageUrl = $"https://en.wikipedia.org/wiki/{Uri.EscapeDataString(pageTitle.Replace(' ', '_'))}";
            return new MeaningSource(Name, pageUrl, text);
        }
        catch (Exception)
        {
            return null; // 조용한 강등
        }
    }

    /// <summary>
    /// 검색 결과 중 <b>이 곡의</b> 문서를 고른다. 확신이 없으면 <b>포기한다</b>(null) —
    /// 여기서 고른 문서가 그대로 LLM의 근거가 되므로, 엉뚱한 문서를 넘기면 그럴듯하고 완전히
    /// 틀린 "의미"가 만들어진다. 자료가 없는 것보다 나쁘다.
    ///
    /// 실측으로 걸린 함정: "(song)"이 붙은 제목을 무조건 우선하면 <c>Kids / MGMT</c> 검색에서
    /// 정답인 <c>Kids (MGMT song)</c>("(song)"이 아니라 "(MGMT song)"이다)를 제치고
    /// 상위에 섞여 있던 <c>Pursuit of Happiness (song)</c>가 뽑혔다. 그래서 제목 일치를 먼저 보고
    /// 아티스트 확인을 요구한다.
    /// </summary>
    private async Task<string?> FindPageAsync(string title, string artist, CancellationToken ct)
    {
        var clean = SearchTermCleaner.Variants(new SearchTerm(title, artist))
            .FirstOrDefault(v => !v.IsKeyword);
        var t = clean?.Title ?? title;
        var a = clean?.Artist ?? artist;

        // 검색어에는 **대표 이름 하나만** 넣는다 — 앨범 꼬리표나 공동 아티스트가 그대로 들어가면
        // 검색이 흐려진다("little freak harry styles — harry's house song").
        var primary = ArtistNames.Primary(a);
        var query = string.IsNullOrWhiteSpace(primary) ? $"{t} song" : $"{t} {primary} song";
        var url = $"{_endpoint}?action=query&format=json&formatversion=2&list=search&srlimit=5"
                + $"&srsearch={Uri.EscapeDataString(query)}";
        var body = await _http.GetFromJsonAsync<SearchEnvelope>(url, Json, ct).ConfigureAwait(false);
        var hits = body?.Query?.Search;
        if (hits is not { Length: > 0 }) return null;

        return PickPage(hits, t, a);
    }

    /// <summary>
    /// 후보 중 가장 그럴듯한 곡 문서를 고른다(순수 함수 — 테스트 대상).
    /// 조건을 못 채우면 null. 점수가 아니라 <b>필수 조건</b>으로 거른다.
    /// </summary>
    internal static string? PickPage(IReadOnlyList<SearchHit> hits, string title, string artist)
    {
        var wantedTitle = Normalize(title);
        if (wantedTitle.Length == 0) return null;
        var wantedArtists = ArtistCandidates(artist);

        SearchHit? best = null;
        var bestScore = int.MinValue;

        foreach (var hit in hits)
        {
            if (hit.Title is not { Length: > 0 } pageTitle) continue;

            // 필수 ①: 문서 제목이 곡 제목을 담아야 한다.
            var normalizedPage = Normalize(pageTitle);
            if (!normalizedPage.Contains(wantedTitle, StringComparison.Ordinal)) continue;

            // 이름 **하나라도** 걸리면 이 곡의 문서로 본다. 합작곡의 문서 제목은
            // "Shallow (Lady Gaga and Bradley Cooper song)"처럼 우리가 받은 표기와 다르게 적히므로,
            // 전원 일치를 요구하면 아무것도 통과하지 못한다.
            var normalizedSnippet = Normalize(hit.Snippet ?? "");
            var titleHasArtist = wantedArtists.Any(n => normalizedPage.Contains(n, StringComparison.Ordinal));
            var snippetHasArtist = wantedArtists.Any(n => normalizedSnippet.Contains(n, StringComparison.Ordinal));

            // 필수 ②: 아티스트를 아는데 제목에도 스니펫에도 없으면 동명이곡일 수 있다 — 버린다.
            if (wantedArtists.Count > 0 && !titleHasArtist && !snippetHasArtist) continue;

            var score =
                  (titleHasArtist ? 4 : 0)                                                    // "Kids (MGMT song)"
                + (pageTitle.Contains("song", StringComparison.OrdinalIgnoreCase) ? 2 : 0)     // 곡 문서 표식
                + (snippetHasArtist ? 1 : 0)
                + (normalizedPage == wantedTitle ? 1 : 0);                                    // 제목이 정확히 일치

            if (score > bestScore) (best, bestScore) = (hit, score);
        }

        return best?.Title;
    }

    /// <summary>
    /// 비교에 쓸 아티스트 이름들. 너무 짧은 조각(<c>AC/DC</c> → "ac","dc")은 아무 데나 걸리므로
    /// 버리고, 그렇게 다 버려지면 원본 전체로 되돌린다 — 확인 자체를 포기하는 것보다 낫다.
    /// </summary>
    private static IReadOnlyList<string> ArtistCandidates(string artist)
    {
        var names = ArtistNames.All(artist)
            .Select(Normalize)
            .Where(n => n.Length >= 3)
            .ToList();

        if (names.Count == 0)
        {
            var whole = Normalize(artist);
            if (whole.Length > 0) names.Add(whole);
        }
        return names;
    }

    private static string Normalize(string s) => MeaningText.Normalize(s);

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // ---- 응답 모델(필요한 필드만) ----

    private sealed record SearchEnvelope(SearchQuery? Query);
    private sealed record SearchQuery(SearchHit[]? Search);

    /// <summary>검색 결과 한 건. <see cref="PickPage"/> 테스트를 위해 internal이다.</summary>
    internal sealed record SearchHit(string? Title, string? Snippet);

    private sealed record QueryEnvelope(PageQuery? Query);
    private sealed record PageQuery(PageEntry[]? Pages);
    private sealed record PageEntry(string? Title, string? Extract);
}

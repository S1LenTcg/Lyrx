using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// 从 Wikipedia(MediaWiki API) 抓取歌曲条目的导言。**不需要密钥。**
/// 知名歌曲会有"Composition"、"Lyrics and interpretation"之类的整节内容，密度最高。
///
/// 搜索用 <c>list=search</c>，但**优先 "(song)" 条目** — 同名专辑、电影条目
/// 被先抓到的情况很常见。正文用 <c>prop=extracts&amp;exintro&amp;explaintext</c> 取。
///
/// 条目正文是 <b>CC BY-SA</b> — 必须在界面上同时标注出处与许可（由调用方负责）。
/// </summary>
public sealed partial class WikipediaSource : ISongMeaningSource
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly TimeSpan _timeout;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <param name="language">维基语言代码。默认英语 — 歌曲解说英文版压倒性地更厚实。</param>
    /// <param name="timeoutMs">搜索 + 正文两次调用整体的预算（参见 <see cref="GeniusSource"/>）。</param>
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
            return null; // 静默降级
        }
    }

    /// <summary>
    /// 从搜索结果里挑出<b>这首歌的</b>条目。没把握就<b>放弃</b>（null）—
    /// 这里挑出的条目会直接成为 LLM 的依据，传错条目就会造出看似像样却完全
    /// 错误的"含义"。那比没有资料更糟。
    ///
    /// 实测踩到的坑：只要无条件优先带 "(song)" 的标题，搜索 <c>Kids / MGMT</c> 时
    /// 就会放过正确答案 <c>Kids (MGMT song)</c>（不是 "(song)"，而是 "(MGMT song)"），
    /// 反而挑中混在上位的 <c>Pursuit of Happiness (song)</c>。所以先看标题是否匹配，
    /// 再要求确认艺术家。
    /// </summary>
    private async Task<string?> FindPageAsync(string title, string artist, CancellationToken ct)
    {
        var clean = SearchTermCleaner.Variants(new SearchTerm(title, artist))
            .FirstOrDefault(v => !v.IsKeyword);
        var t = clean?.Title ?? title;
        var a = clean?.Artist ?? artist;

        // 搜索词里只放**一个代表名** — 若把专辑后缀或合作艺术家一并塞进去，
        // 搜索就会被搅浑（"little freak harry styles — harry's house song"）。
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
    /// 从候选中挑出最像这首歌的条目（纯函数 — 测试对象）。
    /// 条件不满足则为 null。不是按分数筛，而是按<b>必要条件</b>筛。
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

            // 必需 ①：条目标题必须包含歌曲标题。
            var normalizedPage = Normalize(pageTitle);
            if (!normalizedPage.Contains(wantedTitle, StringComparison.Ordinal)) continue;

            // 只要命中**任一个**名字，就视为这首歌的条目。合作曲的条目标题会被写成
            // "Shallow (Lady Gaga and Bradley Cooper song)"这种与我们收到的写法不同的形式，
            // 若要求全员一致，反而什么都过不了。
            var normalizedSnippet = Normalize(hit.Snippet ?? "");
            var titleHasArtist = wantedArtists.Any(n => normalizedPage.Contains(n, StringComparison.Ordinal));
            var snippetHasArtist = wantedArtists.Any(n => normalizedSnippet.Contains(n, StringComparison.Ordinal));

            // 必需 ②：知道艺术家，而标题和摘要里都没有，就可能是同名别的歌 — 丢弃。
            if (wantedArtists.Count > 0 && !titleHasArtist && !snippetHasArtist) continue;

            var score =
                  (titleHasArtist ? 4 : 0)                                                    // "Kids (MGMT song)"
                + (pageTitle.Contains("song", StringComparison.OrdinalIgnoreCase) ? 2 : 0)     // 歌曲条目标记
                + (snippetHasArtist ? 1 : 0)
                + (normalizedPage == wantedTitle ? 1 : 0);                                    // 标题完全一致

            if (score > bestScore) (best, bestScore) = (hit, score);
        }

        return best?.Title;
    }

    /// <summary>
    /// 用于比较的艺术家名字。过短的片段（<c>AC/DC</c> → "ac","dc"）会到处误命中，所以
    /// 丢掉；若全被丢光就退回原始整体 — 这比放弃确认本身要好。
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

    // ---- 响应模型（只保留需要的字段） ----

    private sealed record SearchEnvelope(SearchQuery? Query);
    private sealed record SearchQuery(SearchHit[]? Search);

    /// <summary>一条搜索结果。为了 <see cref="PickPage"/> 的测试而设为 internal。</summary>
    internal sealed record SearchHit(string? Title, string? Snippet);

    private sealed record QueryEnvelope(PageQuery? Query);
    private sealed record PageQuery(PageEntry[]? Pages);
    private sealed record PageEntry(string? Title, string? Extract);
}

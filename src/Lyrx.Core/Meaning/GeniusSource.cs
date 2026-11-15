using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// 通过 Genius 官方 API 获取歌曲的 "About"（<c>description</c>）。
///
/// 只要有免费 Client Access Token 就够，不需要 OAuth 用户流程
/// （在 <see href="https://genius.com/api-clients"/> 签发）。调用两次：
/// 用 <c>GET /search?q=</c> 找到歌曲 id → 再从 <c>GET /songs/{id}?text_format=plain</c> 读取
/// <c>description</c> 与准确的歌曲页 <c>url</c>。
///
/// **歌词正文不会通过这个 API 返回**（那属于抓取范畴）—— 我们已经有了，所以无所谓。
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
    /// **两次顺序调用整体**的预算，所以要给足。实测越有名的歌
    /// 说明越长越慢 —— <c>Lady Gaga / Shallow</c> 是搜索 0.96 秒 + 详情 2.0 秒共 2.95 秒，
    /// 在旧的默认值 2.5 秒之下**恰恰是这类歌被悄悄截掉了**（而这正是资料最好的歌）。
    /// 与歌词搜索不同，这里是用户按下按钮在等，所以漏掉远比延迟糟糕。
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

            // 很多歌的说明是空的（大部分歌没有 About）。那时就当作没有该来源。
            if (string.IsNullOrWhiteSpace(description) || description!.Trim().Length < 40)
                return null;

            return new MeaningSource(Name, url, description.Trim());
        }
        catch (Exception)
        {
            return null; // 静默降级 —— 由其他来源填充
        }
    }

    /// <summary>
    /// 依次尝试原始写法和净化写法。流媒体元数据里的噪声
    /// （feat.、remaster 标注，"• 智能随机播放推荐"这类尾巴）会直接让 Genius 搜索失败。
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
    /// 确认这条搜索结果确实就是那首歌。判定标准由所有基于搜索的来源共享
    /// （<see cref="MeaningMatch.IsSameSong"/> —— 理由写在那一边）。
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

    // ---- 响应模型（只保留需要的字段）----

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

    /// <summary>当 <c>text_format=plain</c> 时，说明以 <c>{"plain": "..."}</c> 的形式返回。</summary>
    private sealed record GeniusDescription([property: JsonPropertyName("plain")] string? Plain);
}

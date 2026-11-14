using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Musebase.Core.Search;

namespace Musebase.Windows.Services;

/// <summary>
/// 当播放器未通过 SMTC 上报封面（如 B 站客户端、浏览器）时，自动向在线音乐库检索官方高清专辑封面。
/// </summary>
internal static class CoverFetcher
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CoverSearchHit(string Url, int Score, bool ArtistMatched);

    /// <summary>
    /// 根据歌名与歌手拉取高清专辑封面字节数据。
    /// </summary>
    public static async Task<byte[]?> FetchCoverAsync(string title, string? artist, Uri? fallbackUri = null, CancellationToken ct = default)
    {
        var cleanTitle = title?.Trim() ?? "";
        var cleanArtist = artist?.Trim() ?? "";
        if (cleanTitle.Length == 0 || ct.IsCancellationRequested) return null;

        var cacheKey = $"{cleanTitle}|{cleanArtist}";
        if (Cache.TryGetValue(cacheKey, out var cachedBytes))
            return cachedBytes;

        byte[]? weakFallbackBytes = null;
        string? weakFallbackSource = null;

        // 1. 优先从 QQ 音乐检索官方高清专辑封面（若仅歌名命中而歌手不一致，先暂存以免抢掉酷狗的精确匹配）
        var (qqBytes, qqArtistMatched) = await FetchFromQQMusicAsync(cleanTitle, cleanArtist, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return null;
        if (qqBytes is not null)
        {
            if (qqArtistMatched)
            {
                Cache[cacheKey] = qqBytes;
                Log.Write($"[cover] 成功从 QQ音乐获取封面: {cleanTitle} ({qqBytes.Length} 字节)");
                return qqBytes;
            }
            weakFallbackBytes = qqBytes;
            weakFallbackSource = "QQ音乐(仅歌名匹配)";
        }

        // 2. 如果传入了已知的封面 URL（如已确认属于当前曲目的歌词元数据）
        if (fallbackUri is not null)
        {
            var bytes = await DownloadImageAsync(fallbackUri.ToString(), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return null;
            if (bytes is not null)
            {
                Cache[cacheKey] = bytes;
                Log.Write($"[cover] 成功从歌词元数据获取封面: {cleanTitle} ({bytes.Length} 字节)");
                return bytes;
            }
        }

        // 3. 回退通过酷狗搜索 API 检索高清专辑封面
        var (kugouBytes, kugouArtistMatched) = await FetchFromKugouAsync(cleanTitle, cleanArtist, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return null;
        if (kugouBytes is not null && (kugouArtistMatched || weakFallbackBytes is null))
        {
            Cache[cacheKey] = kugouBytes;
            Log.Write($"[cover] 成功从在线音乐库获取封面: {cleanTitle} ({kugouBytes.Length} 字节)");
            return kugouBytes;
        }

        if (weakFallbackBytes is not null)
        {
            Cache[cacheKey] = weakFallbackBytes;
            Log.Write($"[cover] 成功从{weakFallbackSource}获取封面: {cleanTitle} ({weakFallbackBytes.Length} 字节)");
            return weakFallbackBytes;
        }

        return null;
    }

    private static async Task<(byte[]? Bytes, bool ArtistMatched)> FetchFromKugouAsync(string cleanTitle, string cleanArtist, CancellationToken ct)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanTitle} {cleanArtist}";
            var url = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(query)}&page=1&pagesize=5&showtype=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, false);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Array)
                return (null, false);

            var hits = new List<CoverSearchHit>();
            foreach (var item in info.EnumerateArray())
            {
                if (!item.TryGetProperty("trans_param", out var trans) ||
                    !trans.TryGetProperty("union_cover", out var unionCover) ||
                    unionCover.GetString() is not { Length: > 0 } coverTpl)
                    continue;

                var candTitle = item.TryGetProperty("songname", out var songProp) ? songProp.GetString() : null;
                var candArtist = item.TryGetProperty("singername", out var singerProp) ? singerProp.GetString() : null;
                var (score, artistMatched) = ScoreCandidate(cleanTitle, cleanArtist, candTitle, candArtist);
                if (score < 0) continue;

                hits.Add(new CoverSearchHit(coverTpl.Replace("{size}", "480"), score, artistMatched));
            }

            return await DownloadBestHitAsync(hits, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, false);
        }
        catch (Exception ex)
        {
            Log.Write($"[cover] 在线获取封面异常: {ex.Message}");
        }

        return (null, false);
    }

    private static async Task<(byte[]? Bytes, bool ArtistMatched)> FetchFromQQMusicAsync(string title, string artist, CancellationToken ct)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(artist) ? title : $"{title} {artist}";
            var body = new
            {
                req_1 = new
                {
                    method = "DoSearchForQQMusicDesktop",
                    module = "music.search.SearchCgiService",
                    param = new { num_per_page = 10, page_num = 1, query, search_type = 0 },
                },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Headers.TryAddWithoutValidation("Referer", "https://y.qq.com/");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, false);

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("req_1", out var req1) ||
                (req1.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.Number && codeProp.GetInt32() != 0) ||
                !req1.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("body", out var bodyEl) ||
                !bodyEl.TryGetProperty("song", out var songEl) ||
                !songEl.TryGetProperty("list", out var listEl) ||
                listEl.ValueKind != JsonValueKind.Array)
                return (null, false);

            var hits = new List<CoverSearchHit>();
            foreach (var item in listEl.EnumerateArray())
            {
                if (!item.TryGetProperty("album", out var album) ||
                    !album.TryGetProperty("mid", out var midProp) ||
                    midProp.GetString() is not { Length: > 0 } albumMid)
                    continue;

                var candTitle = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString()
                    : item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                var candArtist = ExtractQQSingers(item);
                var (score, artistMatched) = ScoreCandidate(title, artist, candTitle, candArtist);
                if (score < 0) continue;

                hits.Add(new CoverSearchHit($"https://y.gtimg.cn/music/photo_new/T002R500x500M000{albumMid}.jpg", score, artistMatched));
            }

            return await DownloadBestHitAsync(hits, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, false);
        }
        catch (Exception ex)
        {
            Log.Write($"[cover] QQ音乐获取封面异常: {ex.Message}");
        }
        return (null, false);
    }

    private static async Task<(byte[]? Bytes, bool ArtistMatched)> DownloadBestHitAsync(List<CoverSearchHit> hits, CancellationToken ct)
    {
        if (hits.Count == 0) return (null, false);
        hits.Sort(static (a, b) => b.Score.CompareTo(a.Score));
        foreach (var hit in hits)
        {
            if (ct.IsCancellationRequested) return (null, false);
            var bytes = await DownloadImageAsync(hit.Url, ct).ConfigureAwait(false);
            if (bytes is not null)
                return (bytes, hit.ArtistMatched);
        }
        return (null, false);
    }

    private static string? ExtractQQSingers(JsonElement item)
    {
        if (!item.TryGetProperty("singer", out var singers) || singers.ValueKind != JsonValueKind.Array)
            return null;
        List<string>? names = null;
        foreach (var s in singers.EnumerateArray())
        {
            if (s.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                (names ??= new List<string>()).Add(name);
        }
        return names is null ? null : string.Join(", ", names);
    }

    private static (int Score, bool ArtistMatched) ScoreCandidate(string targetTitle, string targetArtist, string? candTitle, string? candArtist)
    {
        if (string.IsNullOrWhiteSpace(candTitle)) return (-1, false);

        var rawTarget = targetTitle.Trim();
        var rawCand = candTitle.Trim();
        var normTarget = SearchTermCleaner.CleanTitle(rawTarget);
        var normCand = SearchTermCleaner.CleanTitle(rawCand);
        if (normTarget.Length == 0) normTarget = rawTarget;
        if (normCand.Length == 0) normCand = rawCand;

        int titleScore;
        if (string.Equals(rawCand, rawTarget, StringComparison.OrdinalIgnoreCase))
            titleScore = 25;
        else if (string.Equals(normCand, normTarget, StringComparison.OrdinalIgnoreCase))
            titleScore = 20;
        else if (normTarget.Length >= 2 && normCand.Length >= 2 &&
                 (normCand.Contains(normTarget, StringComparison.OrdinalIgnoreCase) ||
                  normTarget.Contains(normCand, StringComparison.OrdinalIgnoreCase)))
            titleScore = 10;
        else
            return (-1, false);

        if (string.IsNullOrWhiteSpace(targetArtist))
            return (titleScore, true);

        var artistMatched = ArtistsOverlap(targetArtist, candArtist);
        return (titleScore + (artistMatched ? 15 : 0), artistMatched);
    }

    private static bool ArtistsOverlap(string targetArtist, string? candArtist)
    {
        if (string.IsNullOrWhiteSpace(candArtist)) return false;
        var separators = new[] { ',', '/', '、', '&', ';', '×' };
        var targetTokens = targetArtist.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candTokens = candArtist.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var t in targetTokens)
        {
            if (t.Length == 0) continue;
            foreach (var c in candTokens)
            {
                if (c.Length == 0) continue;
                if (string.Equals(t, c, StringComparison.OrdinalIgnoreCase)) return true;
                if (t.Length >= 2 && c.Length >= 2 &&
                    (t.Contains(c, StringComparison.OrdinalIgnoreCase) || c.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        return false;
    }

    private static async Task<byte[]?> DownloadImageAsync(string imageUrl, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (data.Length > 0 && data.Length < 10 * 1024 * 1024)
                    return data;
            }
        }
        catch { }
        return null;
    }
}

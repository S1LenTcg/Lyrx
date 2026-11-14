using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Musebase.Windows.Services;

/// <summary>
/// 当播放器未通过 SMTC 上报封面（如 B 站客户端）时，自动向在线音乐库检索官方高清专辑封面。
/// </summary>
internal static class CoverFetcher
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 根据歌名与歌手拉取高清专辑封面字节数据。
    /// </summary>
    public static async Task<byte[]?> FetchCoverAsync(string title, string? artist, Uri? fallbackUri = null, CancellationToken ct = default)
    {
        var cleanTitle = title?.Trim() ?? "";
        var cleanArtist = artist?.Trim() ?? "";
        if (cleanTitle.Length == 0) return null;

        var cacheKey = $"{cleanTitle}|{cleanArtist}";
        if (Cache.TryGetValue(cacheKey, out var cachedBytes))
            return cachedBytes;

        // 1. 如果传入了已知的封面 URL（如歌词元数据中自带）
        if (fallbackUri is not null)
        {
            var bytes = await DownloadImageAsync(fallbackUri.ToString(), ct).ConfigureAwait(false);
            if (bytes is not null)
            {
                Cache[cacheKey] = bytes;
                return bytes;
            }
        }

        // 2. 通过酷狗搜索 API 检索高清专辑封面
        try
        {
            var query = string.IsNullOrWhiteSpace(cleanArtist) ? cleanTitle : $"{cleanTitle} {cleanArtist}";
            var url = $"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(query)}&page=1&pagesize=1&showtype=1";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("info", out var info) &&
                    info.GetArrayLength() > 0)
                {
                    var first = info[0];
                    if (first.TryGetProperty("trans_param", out var trans) &&
                        trans.TryGetProperty("union_cover", out var unionCover) &&
                        unionCover.GetString() is { Length: > 0 } coverTpl)
                    {
                        var coverUrl = coverTpl.Replace("{size}", "480");
                        var bytes = await DownloadImageAsync(coverUrl, ct).ConfigureAwait(false);
                        if (bytes is not null)
                        {
                            Cache[cacheKey] = bytes;
                            Log.Write($"[cover] 成功从在线音乐库获取封面: {cleanTitle} ({bytes.Length} 字节)");
                            return bytes;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"[cover] 在线获取封面异常: {ex.Message}");
        }

        return null;
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

using System.Text.Json;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>Musixmatch 返回的单个曲目。<see cref="ShareUrl"/> 就是该曲目的官方页面地址。</summary>
public sealed record MusixmatchTrack(long TrackId, string? ShareUrl, string? Name, string? Artist);

/// <summary>
/// 通过 Musixmatch 官方 API（<c>api.musixmatch.com/ws/1.1</c>）找到曲目，得到**精确的曲目页面地址**。
///
/// <b>为什么不能按规则拼地址</b>已被实测证实 —— <c>/lyrics/Pearl-Jam/Even-Flow</c>
/// 会不带任何错误地返回 200，却悄悄跳转到 <c>/lyrics/Pearl-Jam/Alive</c>（另一首歌！）。
/// 这属于把用户送到错误曲目上的失败，所以禁止猜测；而由服务器抓取搜索结果页面这条路
/// 也已被堵住：匿名请求会被重定向到登录页。剩下唯一正当的途径就是这个 API。
///
/// 密钥在 <see href="https://developer.musixmatch.com"/> 申请。没有则静默关闭。
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
            return null; // 来源与链接都是附加功能 —— 即使失败也不应影响歌词
        }
    }

    /// <summary>
    /// 从响应中挑出这首歌（纯函数 —— 是测试对象）。
    ///
    /// <b>即使是 HTTP 200 也可能是失败</b> —— Musixmatch 把成功/失败放在正文的
    /// <c>message.header.status_code</c> 里（密钥错误 401、套餐超限 402 …）。
    /// 无结果时 <c>body</c> 有时不是对象而是空数组，
    /// 因此用 <see cref="JsonDocument"/> 做防御式读取，而不用记录反序列化。
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

            // 搜索 API 无论输入什么都会返回点什么 —— 接收之前先核对。
            if (!MeaningMatch.IsSameSong(name, by, title, artist)) continue;

            var id = track.TryGetProperty("track_id", out var idEl) && idEl.TryGetInt64(out var v) ? v : 0;
            return new MusixmatchTrack(id, Text(track, "track_share_url"), name, by);
        }
        return null;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>按"原始写法 → 清洗后写法"的顺序依次尝试。艺术家只用其代表名。</summary>
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

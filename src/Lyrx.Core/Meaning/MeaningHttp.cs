using System.Net;

namespace Musebase.Core.Meaning;

/// <summary>
/// 含义收集专用的 HttpClient。
///
/// **与歌词提供方分开的理由是 User-Agent。** Wikimedia 要求描述性的 User-Agent，
/// 缺少时 **会返回 403** — .NET 的 <see cref="HttpClient"/> 不发送默认 User-Agent，
/// 因此原样使用的话维基百科源总是静默为空（已实测确认）。若在歌词提供方一侧的
/// <c>LyricsHttp.Client</c> 上添加该头，会连已验证的提供方的行为也一起改动，
/// 所以只在这里添加。
/// </summary>
public static class MeaningHttp
{
    /// <summary>Wikimedia User-Agent 政策要求的格式 — 应用名 + 可联系的地址。</summary>
    public const string UserAgent = "Musebase/1.0 (+https://github.com/countnine/musebase)";

    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        })
        {
            // 实际超时由每次调用链接的 CTS 决定。此值只是必须比它**更长**的兜底 —
            // 从前写死的 30 秒会悄悄截断 OpenRouter writer 的 60 秒（最多 180 秒）预算，使慢模型
            // 总是以"临时性错误"收场。这里比所有调用方的上限（180 秒）留得宽裕些。
            Timeout = TimeSpan.FromMinutes(4),
        };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return client;
    }
}

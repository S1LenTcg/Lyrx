using System.Net;

namespace Musebase.Core.Meaning;

/// <summary>
/// 의미 수집 전용 HttpClient.
///
/// **가사 제공자와 분리한 이유는 User-Agent다.** Wikimedia는 설명적인 User-Agent를 요구하고
/// 없으면 **403을 돌려준다** — .NET의 <see cref="HttpClient"/>는 기본 User-Agent를 보내지 않으므로
/// 그대로 두면 위키피디아 소스가 항상 조용히 빈다(실측으로 확인). 가사 제공자 쪽
/// <c>LyricsHttp.Client</c>에 헤더를 얹으면 이미 검증된 제공자들의 동작까지 건드리게 되므로
/// 여기서만 붙인다.
/// </summary>
public static class MeaningHttp
{
    /// <summary>Wikimedia User-Agent 정책이 요구하는 형식 — 앱 이름 + 연락 가능한 주소.</summary>
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
            // 실제 만료는 호출마다 링크된 CTS로 정한다. 이 값은 그보다 **길어야 하는** 안전망이다 —
            // 예전 30초는 OpenRouter writer의 60초(최대 180초) 예산을 몰래 잘라, 느린 모델이
            // 늘 "일시적 오류"로 끝났다. 모든 호출자의 상한(180초)보다 넉넉히 둔다.
            Timeout = TimeSpan.FromMinutes(4),
        };
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        return client;
    }
}

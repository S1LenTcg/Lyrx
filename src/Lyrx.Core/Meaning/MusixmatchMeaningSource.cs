using System.Text.Json;
using System.Text.RegularExpressions;

namespace Musebase.Core.Meaning;

/// <summary>
/// Musixmatch 곡 페이지의 "Meaning"을 가져온다. <b>기본으로 켜지지 않는다</b> —
/// <c>MUSEBASE_MEANING_SOURCES</c>에 <c>musixmatch</c>를 명시해야 쓰인다.
///
/// <b>이 텍스트는 사람이 쓴 해설이 아니다.</b> 페이지 HTML의 <c>__NEXT_DATA__</c> 안 <c>lens</c>
/// 블록에 들어 있고, 같은 블록에 <c>moods</c>·<c>themes</c>·콘텐츠 등급이 함께 있다 —
/// 가사를 기계로 분석한 묶음이다. 즉 이걸 자료로 쓰면 <b>LLM이 쓴 글을 다시 LLM에 넣어 요약</b>하는
/// 셈이라, 무엇에 근거했는지 추적할 수 없고 다른 소스와 같은 무게로 다루면 안 된다.
/// 그래서 이름에 "(AI 분석)"을 박아 출처 표기에 그대로 드러나게 하고, 프롬프트에서도
/// 다른 자료와 충돌하면 다른 자료를 따르도록 한다. 배경과 결정은 ADR-0007.
///
/// 주소는 <see cref="MusixmatchApi"/>가 준 <c>track_share_url</c>만 쓴다 — 규칙으로 만든 주소는
/// 조용히 다른 곡으로 넘어간다(그쪽 설명 참고).
/// </summary>
public sealed partial class MusixmatchMeaningSource : ISongMeaningSource
{
    /// <summary>이보다 짧으면 근거로 삼지 않는다(Genius와 같은 기준).</summary>
    private const int MinLength = 40;

    private readonly MusixmatchApi _api;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public MusixmatchMeaningSource(MusixmatchApi api, HttpClient? http = null, int timeoutMs = 8000)
    {
        _api = api;
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    /// <summary>출처 표기에 그대로 나간다 — 사람이 쓴 해설처럼 보이면 안 된다.</summary>
    public string Name => "Musixmatch (AI 분석)";

    public async Task<MeaningSource?> FetchAsync(string title, string artist, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            // 주소를 모르면 여기서 끝난다. 추측해서 받아 오지 않는다.
            var track = await _api.FindAsync(title, artist, cts.Token).ConfigureAwait(false);
            if (track?.ShareUrl is not { Length: > 0 } url) return null;

            var html = await _http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            var explanation = Explanation(html);
            if (explanation is null || explanation.Length < MinLength) return null;

            return new MeaningSource(Name, url, explanation);
        }
        catch (Exception)
        {
            return null; // 조용한 강등
        }
    }

    /// <summary>
    /// 페이지에서 의미 문단을 꺼낸다(순수 함수 — 테스트 대상).
    ///
    /// <c>lens</c>까지의 경로를 고정하지 않고 <b>재귀로 찾는다</b> — Next.js 페이지의 데이터 구조는
    /// 우리 사정과 무관하게 바뀌고, 경로를 박아 두면 바뀌는 순간 예외도 없이 조용히 비기 때문이다.
    /// </summary>
    internal static string? Explanation(string html)
    {
        var match = NextDataRegex().Match(html ?? "");
        if (!match.Success) return null;

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            return FindLensMeaning(doc.RootElement)?.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindLensMeaning(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("lens")
                        && property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("meaning", out var meaning)
                        && meaning.ValueKind == JsonValueKind.Object
                        && meaning.TryGetProperty("explanation", out var text)
                        && text.ValueKind == JsonValueKind.String)
                        return text.GetString();

                    if (FindLensMeaning(property.Value) is { } found) return found;
                }
                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (FindLensMeaning(item) is { } found) return found;
                return null;

            default:
                return null;
        }
    }

    [GeneratedRegex("""<script id="__NEXT_DATA__"[^>]*>(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();
}

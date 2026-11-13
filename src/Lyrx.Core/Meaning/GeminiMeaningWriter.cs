using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// Google Gemini Developer API(<c>generativelanguage.googleapis.com</c>)로 의미를 쓴다.
///
/// **Vertex AI가 아니라 Developer API를 쓰는 이유**: 인증이 API 키 한 줄이라
/// <see cref="Translation.GoogleTranslateTranslator"/>와 완전히 같은 패턴이고(서비스 계정·ADC 불필요),
/// 무료 티어가 있어 보유 곡 전체를 0원에 채울 수 있다. IAM·데이터 레지던시 같은 거버넌스가
/// 필요해지면 그때 Vertex로 옮기면 된다.
///
/// 키는 <see href="https://aistudio.google.com/apikey"/>에서 만든다. **$300 무료 체험
/// 크레딧은 Gemini API에 쓸 수 없다**(공식 문서에 명시된 제외 항목) — 별개인 "Gemini API
/// 무료 티어"가 있고, 그건 **결제를 연결하지 않은 프로젝트에만** 적용된다. 결제를 붙이는
/// 순간 그 프로젝트는 Tier 1(유료)이 되고 무료 티어는 사라지므로, 무료로 쓰려면 결제가
/// 없는 프로젝트에서 키를 만들어야 한다. 다만 요약 한 번은 매우 싸서(곡당 사실상 0원)
/// 유료 티어로 두는 선택도 합리적이다 — 그쪽은 보낸 내용이 학습에 쓰이지 않는다.
/// </summary>
public sealed class GeminiMeaningWriter : IMeaningWriter
{
    /// <summary>
    /// 가장 싼 경량 모델. 요약 작업엔 충분하다. <c>gemini-2.5-flash-lite</c>는 2026-09 기준
    /// "신규 사용자에게 더 이상 제공되지 않는다"(404)며 이 모델을 권한다.
    /// </summary>
    public const string DefaultModel = "gemini-3.5-flash-lite";

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;

    public GeminiMeaningWriter(string apiKey, string? model = null, HttpClient? http = null, int timeoutMs = 30_000)
    {
        _apiKey = apiKey.Trim();
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000, 120_000));
    }

    public string EngineId => "gemini";
    public string Model { get; }

    public async Task<MeaningWriteResult> WriteAsync(
        string title, string artist, IReadOnlyList<MeaningSource> sources,
        string targetLang, CancellationToken ct = default)
    {
        if (_apiKey.Length == 0) return MeaningWriteResult.MissingKey();
        if (sources.Count == 0) return MeaningWriteResult.Failed;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var prompt = MeaningPrompt.Build(title, artist, sources, targetLang);
            var payload = new GeminiRequest
            {
                Contents = [new GeminiContent { Parts = [new GeminiPart { Text = prompt }] }],
                GenerationConfig = new GeminiGenerationConfig
                {
                    MaxOutputTokens = MeaningPrompt.MaxOutputTokens,
                },
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{Model}:generateContent")
            {
                Content = JsonContent.Create(payload, options: Json),
            };
            request.Headers.Add("x-goog-api-key", _apiKey);

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 본문을 버리지 않는다 — "잔액 부족"·"모델 없음" 같은 진짜 이유가 여기에만 있다.
                var error = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return MeaningWriteResult.FromStatus(response.StatusCode, error);
            }

            var body = await response.Content.ReadFromJsonAsync<GeminiResponse>(Json, cts.Token).ConfigureAwait(false);
            var text = body?.Candidates?
                .FirstOrDefault()?.Content?.Parts?
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Text))?.Text;
            return string.IsNullOrWhiteSpace(text)
                ? MeaningWriteResult.Failed with { Reason = "응답 본문이 비어 있음" }
                : MeaningWriteResult.Written(text!.Trim());
        }
        catch (OperationCanceledException)
        {
            // 타임아웃이든 호출자 취소든 "결과를 모른다"는 뜻이다 — 실패로 못 박지 않는다.
            return MeaningWriteResult.Transient with { Reason = "응답 시간 초과" };
        }
        catch (HttpRequestException ex)
        {
            return MeaningWriteResult.Transient with { Reason = MeaningWriteResult.ReasonOf(ex.Message) }; // 네트워크는 다음에 될 수 있다
        }
        catch (Exception ex)
        {
            return MeaningWriteResult.Failed with { Reason = ex.GetType().Name }; // 의미는 부가 기능 — 가사에 영향이 없어야 한다
        }
    }

    // ---- 요청/응답 모델(필요한 필드만) ----

    private sealed record GeminiRequest
    {
        public GeminiContent[] Contents { get; init; } = [];
        public GeminiGenerationConfig? GenerationConfig { get; init; }
    }

    /// <summary>출력 상한만 쓴다(<see cref="MeaningPrompt.MaxOutputTokens"/> 참고).</summary>
    private sealed record GeminiGenerationConfig
    {
        public int MaxOutputTokens { get; init; }
    }

    private sealed record GeminiContent
    {
        public GeminiPart[] Parts { get; init; } = [];
    }

    private sealed record GeminiPart
    {
        public string? Text { get; init; }
    }

    private sealed record GeminiResponse(GeminiCandidate[]? Candidates);
    private sealed record GeminiCandidate(GeminiContentOut? Content);
    private sealed record GeminiContentOut(GeminiPart[]? Parts);
}

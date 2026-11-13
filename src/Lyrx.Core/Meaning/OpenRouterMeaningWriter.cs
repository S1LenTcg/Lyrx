using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// OpenRouter로 의미를 쓴다 — **모델을 갈아끼우기 위한 엔진**이다.
///
/// 엔드포인트가 OpenAI 호환(<c>/api/v1/chat/completions</c>)이라 키 하나로 Claude·Gemini·GPT·Llama를
/// 모두 부를 수 있고, 바꾸는 것은 <c>model</c> 문자열 하나뿐이다(예: <c>anthropic/claude-opus-5</c>,
/// <c>google/gemini-2.5-flash</c>). 같은 곡을 여러 모델로 만들어 문장 품질을 비교할 때 쓴다.
///
/// 대가로 플랫폼 수수료가 붙으므로, 대량 백필의 기본값은 무료 티어가 있는
/// <see cref="GeminiMeaningWriter"/> 쪽이 낫다.
/// </summary>
public sealed class OpenRouterMeaningWriter : IMeaningWriter
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    public const string DefaultModel = "google/gemini-2.5-flash";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;

    public OpenRouterMeaningWriter(string apiKey, string? model = null, HttpClient? http = null, int timeoutMs = 60_000)
    {
        _apiKey = apiKey.Trim();
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000, 180_000));
    }

    public string EngineId => "openrouter";
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

            var payload = new ChatRequest
            {
                Model = Model,
                Messages =
                [
                    new ChatMessage
                    {
                        Role = "user",
                        Content = MeaningPrompt.Build(title, artist, sources, targetLang),
                    },
                ],
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(payload, options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            // OpenRouter가 순위·통계에 쓰는 선택 헤더. 넣어 두면 대시보드에서 어떤 앱인지 보인다.
            request.Headers.Add("X-Title", "Musebase");
            request.Headers.Add("HTTP-Referer", "https://github.com/countnine/musebase");

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 본문을 버리지 않는다 — "잔액 부족"·"모델 없음" 같은 진짜 이유가 여기에만 있다.
                var error = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return MeaningWriteResult.FromStatus(response.StatusCode, error);
            }

            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cts.Token).ConfigureAwait(false);
            var text = body?.Choices?.FirstOrDefault()?.Message?.Content;
            return string.IsNullOrWhiteSpace(text)
                ? MeaningWriteResult.Failed with { Reason = "응답 본문이 비어 있음" }
                : MeaningWriteResult.Written(text!.Trim());
        }
        catch (OperationCanceledException)
        {
            return MeaningWriteResult.Transient with { Reason = "응답 시간 초과" }; // 타임아웃·취소 — 결과를 모른다
        }
        catch (HttpRequestException ex)
        {
            return MeaningWriteResult.Transient with { Reason = MeaningWriteResult.ReasonOf(ex.Message) }; // 네트워크는 다음에 될 수 있다
        }
        catch (Exception ex)
        {
            return MeaningWriteResult.Failed with { Reason = ex.GetType().Name }; // 조용한 강등
        }
    }

    // ---- 요청/응답 모델(OpenAI 호환, 필요한 필드만) ----

    private sealed record ChatRequest
    {
        public string Model { get; init; } = "";
        public ChatMessage[] Messages { get; init; } = [];

        /// <summary>
        /// 반드시 보낸다. 비워 두면 OpenRouter가 모델 최대치(실측 65,535)를 예약하려 들고,
        /// 잔액이 그만큼을 감당 못 하면 <b>402</b>로 거절한다 — 정작 우리가 쓰는 건 몇백 토큰이다.
        /// 상한을 두면 폭주 비용도 함께 막힌다.
        /// </summary>
        [property: JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; } = MeaningPrompt.MaxOutputTokens;
    }

    private sealed record ChatMessage
    {
        public string Role { get; init; } = "user";
        public string Content { get; init; } = "";
    }

    private sealed record ChatResponse(ChatChoice[]? Choices);
    private sealed record ChatChoice(ChatMessageOut? Message);
    private sealed record ChatMessageOut(string? Content);
}

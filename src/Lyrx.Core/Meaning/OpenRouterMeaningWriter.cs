using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// 通过 OpenRouter 写含义 — 这是一个**用于更换模型的引擎**。
///
/// 端点兼容 OpenAI（<c>/api/v1/chat/completions</c>），所以用一个密钥就能调用 Claude·Gemini·GPT·Llama，
/// 要换的只是 <c>model</c> 字符串本身（例如 <c>anthropic/claude-opus-5</c>、
/// <c>google/gemini-2.5-flash</c>）。在把同一首歌交给多个模型生成、比较句子质量时使用。
///
/// 代价是要付平台手续费，所以大批量回填的默认值更适合选、
/// 有免费额度的 <see cref="GeminiMeaningWriter"/>。
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
            // OpenRouter 用于排序·统计的可选头。带上它，仪表盘里就能看出是哪个应用。
            request.Headers.Add("X-Title", "Musebase");
            request.Headers.Add("HTTP-Referer", "https://github.com/countnine/musebase");

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 不丢弃响应正文 — 像"余额不足"·"模型不存在"这样的真实原因只存在于这里。
                var error = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return MeaningWriteResult.FromStatus(response.StatusCode, error);
            }

            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cts.Token).ConfigureAwait(false);
            var text = body?.Choices?.FirstOrDefault()?.Message?.Content;
            return string.IsNullOrWhiteSpace(text)
                ? MeaningWriteResult.Failed with { Reason = "响应正文为空" }
                : MeaningWriteResult.Written(text!.Trim());
        }
        catch (OperationCanceledException)
        {
            return MeaningWriteResult.Transient with { Reason = "响应超时" }; // 超时·取消 — 结果未知
        }
        catch (HttpRequestException ex)
        {
            return MeaningWriteResult.Transient with { Reason = MeaningWriteResult.ReasonOf(ex.Message) }; // 网络问题下次可能就好了
        }
        catch (Exception ex)
        {
            return MeaningWriteResult.Failed with { Reason = ex.GetType().Name }; // 静默降级
        }
    }

    // ---- 请求/响应模型（兼容 OpenAI，只保留需要的字段） ----

    private sealed record ChatRequest
    {
        public string Model { get; init; } = "";
        public ChatMessage[] Messages { get; init; } = [];

        /// <summary>
        /// 必须发送。留空的话 OpenRouter 会试图预留模型的最大值（实测 65,535），
        /// 而余额撑不住那么多就会被 <b>402</b> 拒绝 — 可我们实际只写几百个 token。
        /// 设个上限也一并挡住了失控的开销。
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

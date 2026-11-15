using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Meaning;

/// <summary>
/// 通过 Google Gemini Developer API（<c>generativelanguage.googleapis.com</c>）撰写意义。
///
/// **用 Developer API 而非 Vertex AI 的原因**：认证只需一行 API 密钥，
/// 与 <see cref="Translation.GoogleTranslateTranslator"/> 完全同一套模式（无需服务账号、无需 ADC），
/// 而且有免费额度，可以用 0 元补齐整个曲库。若日后需要 IAM、数据驻留这类治理能力，
/// 到那时再迁到 Vertex 即可。
///
/// 密钥在 <see href="https://aistudio.google.com/apikey"/> 创建。**300 美元的免费试用
/// 额度不能用于 Gemini API**（官方文档明确列为排除项）—— 另有一个独立的"Gemini API
/// 免费额度"，它只对**未绑定结算的项目**生效。一旦绑定结算，
/// 该项目就成为 Tier 1（付费），免费额度随即消失；所以想免费使用，
/// 就必须在未绑定结算的项目里创建密钥。不过摘要单次极其便宜（每首歌实际上 0 元），
/// 因此留在付费档也是合理选择 —— 那一档不会把发送的内容用于训练。
/// </summary>
public sealed class GeminiMeaningWriter : IMeaningWriter
{
    /// <summary>
    /// 最便宜的轻量模型，做摘要足够。<c>gemini-2.5-flash-lite</c> 自 2026-09 起
    /// 提示"不再向新用户提供"（404），并推荐改用本模型。
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
                // 不丢弃响应正文 —— "余额不足"、"模型不存在"之类的真实原因只存在于这里。
                var error = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                return MeaningWriteResult.FromStatus(response.StatusCode, error);
            }

            var body = await response.Content.ReadFromJsonAsync<GeminiResponse>(Json, cts.Token).ConfigureAwait(false);
            var text = body?.Candidates?
                .FirstOrDefault()?.Content?.Parts?
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Text))?.Text;
            return string.IsNullOrWhiteSpace(text)
                ? MeaningWriteResult.Failed with { Reason = "响应正文为空" }
                : MeaningWriteResult.Written(text!.Trim());
        }
        catch (OperationCanceledException)
        {
            // 无论是超时还是调用方取消，都表示"结果未知" —— 不要一口咬定为失败。
            return MeaningWriteResult.Transient with { Reason = "响应超时" };
        }
        catch (HttpRequestException ex)
        {
            return MeaningWriteResult.Transient with { Reason = MeaningWriteResult.ReasonOf(ex.Message) }; // 网络下次可能就通了
        }
        catch (Exception ex)
        {
            return MeaningWriteResult.Failed with { Reason = ex.GetType().Name }; // 意义只是附加功能 —— 不应影响歌词
        }
    }

    // ---- 请求/响应模型（仅保留需要的字段）----

    private sealed record GeminiRequest
    {
        public GeminiContent[] Contents { get; init; } = [];
        public GeminiGenerationConfig? GenerationConfig { get; init; }
    }

    /// <summary>只使用输出上限（参见 <see cref="MeaningPrompt.MaxOutputTokens"/>）。</summary>
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

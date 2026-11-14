using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>
/// 通过 OpenRouter(兼容 OpenAI 的 <c>/chat/completions</c>)翻译歌词 ——
/// 它是<b>用来替换模型的引擎</b>。一个密钥就能调用 Claude、Gemini、GPT、Llama，
/// 而切换的只是 <c>model</c> 字符串本身。
///
/// 它的性质与机器翻译不同。DeepL、Google 是一行进、一行出，LLM 却
/// <b>可能不守住行数</b> —— 它会把两行合成一句，或额外添上说明。歌词的行
/// 就是时间轴，只要错开一行，整屏都会错位，所以收到的行数不一致时，<b>就把整块
/// 丢弃</b>(全部为 null = 视为没有翻译)。比起翻了一半的译文，没有译文更好。
///
/// <b>块的大小决定行数准确率</b>(2026-09 实测，积压 10 首歌 806 行)：在 40 行一块时，
/// 任何模型都守不住契约(24 块中有 2~4 块被丢弃)，缩到 20 行后 <c>gemini-2.5-flash-lite</c>
/// 守住了全部 44 块。翻译<b>专用</b>模型(Tencent HY-MT2)反而更差 —— 因为我们要
/// 求的不是翻译质量，而是指令遵循。
///
/// 代价是要付平台手续费。如果只是大量刷歌的用途，有免费额度的方案更好 ——
/// 这个引擎留着是为了看「这首歌由这个模型会怎么转写」。
/// </summary>
public sealed class OpenRouterTranslator : ITranslator
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    /// <summary>不选模型时就用它 —— 与意义生成侧相同的默认值(便宜又快)。</summary>
    public const string DefaultModel = "google/gemini-2.5-flash";

    /// <summary>
    /// 一次发送的行数。<b>这个值决定行数准确率。</b>2026-09 实测(积压 10 首歌 806 行)：
    /// 在 40 行一块时任何模型都守不住契约(24 块中有 2~4 块被丢弃)，<b>缩到 20 行后
    /// <c>gemini-2.5-flash-lite</c>守住了全部 44 块</b>。太长模型会漏掉中间部分。
    /// </summary>
    public const int DefaultMaxLinesPerRequest = 20;

    /// <summary>一次发送的累计字符数 —— 即使混入长行，请求也不会变得臃肿。</summary>
    public const int DefaultMaxCharsPerRequest = 1800;

    /// <summary>输出 token 上限的封顶值。各模型可能更低，可用构造函数收窄。</summary>
    public const int DefaultMaxOutputTokens = 8000;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly TimeSpan _timeout;
    private readonly int _maxOutputTokens;
    private readonly int _maxLines;
    private readonly int _maxChars;

    /// <param name="maxOutputTokens">
    /// 该模型接受的输出 token 上限(留空则用 <see cref="DefaultMaxOutputTokens"/>)。
    /// <b>块的大小由此派生</b> —— 两个值分开放，就会出现「上限是 4096，却请求 8000」的
    /// 组合，那样响应会被截断，整块被丢弃。人只需要填一个上限即可。
    /// </param>
    public OpenRouterTranslator(
        string apiKey, string? model = null, HttpClient? http = null, int timeoutMs = 60_000,
        int? maxOutputTokens = null)
    {
        _apiKey = (apiKey ?? "").Trim();
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model!.Trim();
        _http = http ?? LyricsHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000, 180_000));
        _maxOutputTokens = Math.Clamp(maxOutputTokens ?? DefaultMaxOutputTokens, 512, 32_000);

        // 从输出上限反推块大小。因为 max_tokens = 输入长度*2 + 256，所以这里取它的逆运算。
        _maxChars = Math.Clamp((_maxOutputTokens - 256) / 2, 200, DefaultMaxCharsPerRequest);
        _maxLines = DefaultMaxLinesPerRequest;
    }

    public string Model { get; }

    public async Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        var results = new string?[texts.Count];
        if (_apiKey.Length == 0 || texts.Count == 0) return results;

        var language = LanguageName(targetLang);

        for (var offset = 0; offset < texts.Count;)
        {
            var count = ChunkSize(texts, offset);
            var chunk = texts.Skip(offset).Take(count).ToList();

            var translated = await TranslateChunkAsync(chunk, language, ct).ConfigureAwait(false);
            if (translated is not null)
                for (var i = 0; i < count; i++) results[offset + i] = translated[i];

            offset += count;
        }

        return results;
    }

    /// <summary>一个块。行数不一致或失败时为 <c>null</c> —— 调用方就把这一块留空。</summary>
    private async Task<IReadOnlyList<string?>?> TranslateChunkAsync(
        IReadOnlyList<string> lines, string language, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var payload = new ChatRequest
            {
                Model = Model,
                // 不需要创造性 —— 同样的行得到同样的结果，对缓存也更友好。
                Temperature = 0,
                MaxTokens = Math.Clamp((lines.Sum(l => l.Length) * 2) + 256, 256, _maxOutputTokens),
                Messages = [new ChatMessage { Role = "user", Content = Prompt(lines, language) }],
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(payload, options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            // 用于在 OpenRouter 面板上显示是哪个应用的可选请求头。
            request.Headers.Add("X-Title", "Musebase");
            request.Headers.Add("HTTP-Referer", "https://github.com/countnine/musebase");

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            // 把状态码原样带上去 —— 回退链需要区分 456(额度)和 401(密钥)，
            // 如果在这里吞成 null，这些信息就整个消失，等同于「什么都没发生」。
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cts.Token).ConfigureAwait(false);
            var text = body?.Choices?.FirstOrDefault()?.Message?.Content;
            // 行数不一致、JSON 解析失败是**那块内容的问题，不是引擎死了** ——
            // 就那样留成 null，让回退只把这几行交给下一个引擎。
            return text is null ? null : ParseLines(text, lines.Count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 这是我们自己设的超时(不是外层取消) → 上报为失败。
            throw new TimeoutException($"OpenRouter 响应未在 {_timeout.TotalSeconds:0} 秒内返回。");
        }
    }

    private static string Prompt(IReadOnlyList<string> lines, string language)
    {
        var sb = new StringBuilder();
        sb.Append("Translate each line of these song lyrics into ").Append(language).Append(".\n\n")
          .Append("Rules:\n")
          .Append("- Return ONLY a JSON array of strings. No prose, no code fences, no keys.\n")
          .Append("- The array MUST have exactly ").Append(lines.Count).Append(" items, in the same order.\n")
          .Append("- Translate each line on its own. Do NOT merge, split, reorder or drop lines.\n")
          .Append("- Use an empty string for a line that is empty or has nothing to translate.\n")
          .Append("- Keep it natural and singable; do not explain or annotate.\n\n")
          .Append("Lines:\n");

        // 以 JSON 数组形式放入 —— 即使行内有引号，边界也不会被弄乱。
        sb.Append(JsonSerializer.Serialize(lines));
        return sb.ToString();
    }

    /// <summary>
    /// 把响应变成行列表。<b>行数不同就返回 null</b> —— 只要错开一行，整篇歌词就会错位。
    /// 喜欢套代码围栏的模型很常见，所以先剥掉。
    /// </summary>
    internal static IReadOnlyList<string?>? ParseLines(string content, int expected)
    {
        var text = content.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline < 0 || lastFence <= firstNewline) return null;
            text = text[(firstNewline + 1)..lastFence].Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var items = document.RootElement.EnumerateArray().ToList();
            if (items.Count != expected) return null;

            var result = new string?[expected];
            for (var i = 0; i < expected; i++)
            {
                var value = items[i].ValueKind == JsonValueKind.String ? items[i].GetString() : null;
                result[i] = string.IsNullOrWhiteSpace(value) ? null : value;
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private int ChunkSize(IReadOnlyList<string> texts, int offset)
    {
        var count = 0;
        var chars = 0;
        while (offset + count < texts.Count && count < _maxLines)
        {
            var length = texts[offset + count].Length;
            if (count > 0 && chars + length > _maxChars) break;
            chars += length;
            count++;
        }
        return Math.Max(1, count);
    }

    /// <summary>
    /// 给出的不是代码而是<b>语言名称</b> —— LLM 对 "Traditional Chinese" 的理解比 <c>ZH-HANT</c> 更好。
    /// 不认识的代码原样传过去(模型多半能看懂，即使猜错也有行数校验兜着)。
    /// </summary>
    internal static string LanguageName(string? targetLang)
    {
        var upper = (targetLang ?? "").Trim().ToUpperInvariant();
        if (upper.Length == 0) return "English";

        return upper switch
        {
            "KO" => "Korean",
            "JA" => "Japanese",
            "ZH" or "ZH-HANS" or "ZH-CN" => "Simplified Chinese",
            "ZH-HANT" or "ZH-TW" => "Traditional Chinese",
            "EN" or "EN-US" or "EN-GB" => "English",
            "ES" => "Spanish",
            "FR" => "French",
            "DE" => "German",
            "IT" => "Italian",
            "PT" or "PT-BR" or "PT-PT" => "Portuguese",
            "RU" => "Russian",
            "PL" => "Polish",
            "TR" => "Turkish",
            "NL" => "Dutch",
            "UK" => "Ukrainian",
            "CS" => "Czech",
            "VI" => "Vietnamese",
            "ID" => "Indonesian",
            "AR" => "Arabic",
            "NB" or "NO" => "Norwegian",
            _ => upper,
        };
    }

    // ---- 请求/响应模型(兼容 OpenAI，只保留必要字段) ----

    private sealed record ChatRequest
    {
        public string Model { get; init; } = "";
        public ChatMessage[] Messages { get; init; } = [];
        public double Temperature { get; init; }

        /// <summary>
        /// 必须发送。留空的话 OpenRouter 会想把模型最大值预约下来，
        /// 余额承受不住就返回 <b>402</b> 拒绝(意义生成侧实测到的坑)。
        /// </summary>
        [property: JsonPropertyName("max_tokens")]
        public int MaxTokens { get; init; } = 2000;
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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>
/// OpenRouter(OpenAI 호환 <c>/chat/completions</c>)로 가사를 번역한다 —
/// <b>모델을 갈아끼우기 위한 엔진</b>이다. 키 하나로 Claude·Gemini·GPT·Llama를 부를 수 있고
/// 바꾸는 것은 <c>model</c> 문자열뿐이다.
///
/// 기계 번역기와 성격이 다르다. DeepL·Google은 한 줄을 넣으면 한 줄이 나오지만 LLM은
/// <b>줄 수를 지키지 않을 수 있다</b> — 두 줄을 한 문장으로 합치거나 설명을 덧붙인다. 가사는 줄이
/// 곧 타이밍이라 하나만 밀려도 화면 전체가 어긋나므로, 받은 줄 수가 다르면 <b>그 묶음을 통째로
/// 버린다</b>(전부 null = 번역 없음). 반쯤 맞은 번역보다 번역이 없는 편이 낫다.
///
/// <b>묶음 크기가 줄 수 정확도를 좌우한다</b>(2026-09 실측, 백로그 10곡 806줄): 40줄 묶음에서는
/// 어느 모델도 계약을 못 지켰고(24묶음 중 2~4개 폐기), 20줄로 줄이자 <c>gemini-2.5-flash-lite</c>가
/// 44묶음 전부를 지켰다. 번역 <b>전용</b> 모델(Tencent HY-MT2)은 오히려 더 나빴다 — 우리가 요구하는
/// 것은 번역 품질이 아니라 지시 따르기이기 때문이다.
///
/// 대가로 플랫폼 수수료가 붙는다. 곡을 많이 도는 용도라면 무료 티어가 있는 쪽이 낫다 —
/// 이 엔진은 "이 곡을 이 모델이 어떻게 옮기나" 보려고 두는 것이다.
/// </summary>
public sealed class OpenRouterTranslator : ITranslator
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    /// <summary>모델을 안 고르면 이것 — 의미 생성 쪽과 같은 기본값(싸고 빠르다).</summary>
    public const string DefaultModel = "google/gemini-2.5-flash";

    /// <summary>
    /// 한 번에 보낼 줄 수. <b>이 값이 줄 수 정확도를 좌우한다.</b> 2026-09 실측(백로그 10곡 806줄):
    /// 40줄 묶음에서는 어느 모델도 계약을 못 지켰고(24묶음 중 2~4개 폐기), <b>20줄로 줄이자
    /// <c>gemini-2.5-flash-lite</c>가 44묶음 전부를 지켰다</b>. 길면 모델이 중간을 빼먹는다.
    /// </summary>
    public const int DefaultMaxLinesPerRequest = 20;

    /// <summary>한 번에 보낼 누적 글자 수 — 긴 줄이 섞여도 요청이 비대해지지 않게.</summary>
    public const int DefaultMaxCharsPerRequest = 1800;

    /// <summary>출력 토큰 상한의 천장. 모델마다 더 낮을 수 있어 생성자로 좁힐 수 있다.</summary>
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
    /// 이 모델이 받아 주는 출력 토큰 상한(비우면 <see cref="DefaultMaxOutputTokens"/>).
    /// <b>묶음 크기가 여기서 파생된다</b> — 두 값을 따로 두면 "상한은 4096인데 8000을 요청하는"
    /// 조합이 생기고, 그러면 응답이 잘려 묶음이 통째로 버려진다. 사람이 상한 하나만 적으면 된다.
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

        // 출력 상한에서 묶음을 역산한다. max_tokens = 입력길이*2 + 256 이므로 그 역이다.
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

    /// <summary>한 묶음. 줄 수가 어긋나거나 실패하면 <c>null</c> — 호출자는 그 묶음을 비워 둔다.</summary>
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
                // 창의성은 필요 없다 — 같은 줄은 같게 나오는 편이 캐시에도 좋다.
                Temperature = 0,
                MaxTokens = Math.Clamp((lines.Sum(l => l.Length) * 2) + 256, 256, _maxOutputTokens),
                Messages = [new ChatMessage { Role = "user", Content = Prompt(lines, language) }],
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(payload, options: Json),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            // OpenRouter 대시보드에서 어떤 앱인지 보이게 하는 선택 헤더.
            request.Headers.Add("X-Title", "Musebase");
            request.Headers.Add("HTTP-Referer", "https://github.com/countnine/musebase");

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            // 상태 코드를 실어 올린다 — 폴백 체인이 456(한도)·401(키)을 구분해야 하는데,
            // 여기서 null로 삼키면 그 정보가 통째로 사라져 "아무 일도 안 일어난 것"과 같아진다.
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cts.Token).ConfigureAwait(false);
            var text = body?.Choices?.FirstOrDefault()?.Message?.Content;
            // 줄 수 불일치·JSON 파싱 실패는 **엔진이 죽은 게 아니라 그 묶음의 내용 문제**다 —
            // 그대로 null로 두어 폴백이 그 줄만 다음 엔진으로 넘기게 한다.
            return text is null ? null : ParseLines(text, lines.Count);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 우리가 건 타임아웃이다(바깥 취소가 아니다) → 실패로 올린다.
            throw new TimeoutException($"OpenRouter 응답이 {_timeout.TotalSeconds:0}초 안에 오지 않았습니다.");
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

        // JSON 배열로 넣는다 — 줄 안에 따옴표가 있어도 경계가 흔들리지 않는다.
        sb.Append(JsonSerializer.Serialize(lines));
        return sb.ToString();
    }

    /// <summary>
    /// 응답을 줄 목록으로. <b>줄 수가 다르면 null</b> — 하나만 밀려도 가사 전체가 어긋난다.
    /// 코드펜스를 두르는 모델이 흔해 먼저 벗긴다.
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
    /// 코드 대신 <b>언어 이름</b>을 준다 — LLM은 <c>ZH-HANT</c>보다 "Traditional Chinese"를 잘 알아듣는다.
    /// 모르는 코드는 그대로 넘긴다(모델이 알아보는 경우가 많고, 틀려도 줄 수 검사가 막아 준다).
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

    // ---- 요청/응답 모델(OpenAI 호환, 필요한 필드만) ----

    private sealed record ChatRequest
    {
        public string Model { get; init; } = "";
        public ChatMessage[] Messages { get; init; } = [];
        public double Temperature { get; init; }

        /// <summary>
        /// 반드시 보낸다. 비워 두면 OpenRouter가 모델 최대치를 예약하려 들고, 잔액이 그만큼을
        /// 감당 못 하면 <b>402</b>로 거절한다(의미 생성 쪽에서 실측한 함정).
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

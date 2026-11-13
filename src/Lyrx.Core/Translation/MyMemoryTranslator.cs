using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>responseStatus 以数字（200）或字符串（"200"）形式出现时，都按字符串读取。</summary>
internal sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetInt64().ToString(),
            JsonTokenType.Null => null,
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// MyMemory 翻译器（免密钥免费·官方 API）。原文语言用 <c>Autodetect</c> 自动检测。
/// 有匿名单日额度（约 5000 词），耗尽时抛异常，让上层的回退/提示得以生效。
/// 填了邮箱（<see cref="TranslatorOptions.MyMemoryEmail"/>）额度会提高（可选）。
/// 目标语言会把 DeepL 风格代码（KO/EN-US/ZH-HANT）降为 ISO 639-1（ko/en/zh）后传递。
/// </summary>
public sealed class MyMemoryTranslator : ITranslator
{
    private const string Endpoint = "https://api.mymemory.translated.net/get";
    private readonly HttpClient _http;
    private readonly string? _email;

    public MyMemoryTranslator(string? email = null, HttpClient? http = null)
    {
        _email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        _http = http ?? LyricsHttp.Client;
    }

    public async Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        var target = targetLang.Split('-')[0].ToLowerInvariant(); // EN-US → en, ZH-HANT → zh
        var results = new string?[texts.Count];

        // MyMemory 只支持单条 q → 按行顺序请求（为缓解额度压力避免并行）。有缓存时每首歌只调用一次。
        for (var i = 0; i < texts.Count; i++)
        {
            var q = texts[i];
            if (string.IsNullOrWhiteSpace(q)) continue;

            var url = $"{Endpoint}?q={Uri.EscapeDataString(q)}&langpair={Uri.EscapeDataString($"Autodetect|{target}")}";
            if (_email is not null) url += $"&de={Uri.EscapeDataString(_email)}";

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<MyMemoryResponse>(ct).ConfigureAwait(false);

            // responseStatus 可能是字符串也可能是数字。不是 200 即为失败。
            if (body?.ResponseStatus is { } status && status != "200")
                throw MakeFailure(status, body.ResponseDetails);

            var text = body?.ResponseData?.TranslatedText;
            // 额度耗尽等情况仍返回 200，但会在译文位置放入大写警告文本。
            if (text is null || IsWarning(text))
                throw MakeFailure("429", text);

            results[i] = text;
        }

        return results;
    }

    // 检测到超额 → 转成 HttpRequestException，以便归类为 429（RateLimit）。
    private static HttpRequestException MakeFailure(string status, string? detail)
    {
        var code = status == "429" || (detail?.Contains("USED ALL", StringComparison.OrdinalIgnoreCase) ?? false)
            ? HttpStatusCode.TooManyRequests
            : HttpStatusCode.BadGateway;
        return new HttpRequestException($"MyMemory {status}: {detail}", null, code);
    }

    private static bool IsWarning(string text) =>
        text.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)
        || text.Contains("USED ALL AVAILABLE FREE TRANSLATIONS", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("PLEASE SELECT TWO DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase);

    private sealed record MyMemoryResponse(
        [property: JsonPropertyName("responseData")] MyMemoryData? ResponseData,
        [property: JsonPropertyName("responseStatus")]
        [property: JsonConverter(typeof(FlexibleStringConverter))] string? ResponseStatus,
        [property: JsonPropertyName("responseDetails")] string? ResponseDetails);

    private sealed record MyMemoryData(
        [property: JsonPropertyName("translatedText")] string? TranslatedText);
}

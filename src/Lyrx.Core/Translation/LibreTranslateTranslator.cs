using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>
/// LibreTranslate 翻译器(开源·无需密钥)。用 source="auto" 自动识别原文语言。
/// 公共实例存在收费化/限流变化的可能，因此支持更换端点(自托管)。
/// 目标语言从 DeepL 风格代码(KO/EN-US/ZH-HANT)降级为 ISO 639-1(ko/en/zh)后传入。
/// </summary>
public sealed class LibreTranslateTranslator : ITranslator
{
    /// <summary>单次请求的最大文本数(减轻公共实例负载)。</summary>
    private const int MaxTextsPerRequest = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // 没有 api_key 时省略该字段
    };

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string? _apiKey;

    public LibreTranslateTranslator(string endpoint, string? apiKey = null, HttpClient? http = null)
    {
        _endpoint = endpoint.TrimEnd('/') + "/translate";
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _http = http ?? LyricsHttp.Client;
    }

    public async Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        var target = targetLang.Split('-')[0].ToLowerInvariant(); // EN-US → en, ZH-HANT → zh
        var results = new string?[texts.Count];

        for (var offset = 0; offset < texts.Count; offset += MaxTextsPerRequest)
        {
            var chunk = texts.Skip(offset).Take(MaxTextsPerRequest).ToList();

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(
                    new LibreRequest(chunk, "auto", target, "text", _apiKey), options: JsonOptions),
            };

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<LibreResponse>(ct).ConfigureAwait(false);

            // q 为数组时，translatedText 也会以数组返回。
            if (body?.TranslatedText is { } arr)
            {
                for (var i = 0; i < arr.Count && offset + i < results.Length; i++)
                    results[offset + i] = arr[i];
            }
        }

        return results;
    }

    private sealed record LibreRequest(
        [property: JsonPropertyName("q")] IReadOnlyList<string> Q,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("api_key")] string? ApiKey);

    private sealed record LibreResponse(
        [property: JsonPropertyName("translatedText")] List<string>? TranslatedText);
}

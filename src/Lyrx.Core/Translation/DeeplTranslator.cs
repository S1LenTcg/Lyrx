using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>
/// DeepL API v2 翻译器。
/// 密钥以 ":fx" 结尾则为免费档（api-free.deepl.com），否则为专业档（api.deepl.com）。
/// </summary>
public sealed class DeeplTranslator : ITranslator
{
    /// <summary>DeepL 单次请求的最大文本数</summary>
    private const int MaxTextsPerRequest = 50;

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;

    public DeeplTranslator(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _http = http ?? LyricsHttp.Client;
        _endpoint = apiKey.EndsWith(":fx", StringComparison.Ordinal)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";
    }

    public async Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        var results = new string?[texts.Count];

        for (var offset = 0; offset < texts.Count; offset += MaxTextsPerRequest)
        {
            var chunk = texts.Skip(offset).Take(MaxTextsPerRequest).ToList();

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {_apiKey}");
            request.Content = JsonContent.Create(new DeeplRequest(chunk, targetLang));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<DeeplResponse>(ct).ConfigureAwait(false);

            if (body?.Translations is { } translations)
            {
                for (var i = 0; i < translations.Count && offset + i < results.Length; i++)
                    results[offset + i] = translations[i].Text;
            }
        }

        return results;
    }

    private sealed record DeeplRequest(
        [property: JsonPropertyName("text")] IReadOnlyList<string> Text,
        [property: JsonPropertyName("target_lang")] string TargetLang);

    private sealed record DeeplResponse(
        [property: JsonPropertyName("translations")] List<DeeplTranslation>? Translations);

    private sealed record DeeplTranslation(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("detected_source_language")] string? DetectedSourceLanguage);
}

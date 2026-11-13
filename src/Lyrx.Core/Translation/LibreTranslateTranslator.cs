using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>
/// LibreTranslate 번역기(오픈소스·무키). source="auto"로 원문 언어를 자동 감지한다.
/// 공개 인스턴스는 유료화/레이트리밋 변동이 있어 엔드포인트를 교체(자체호스팅)할 수 있게 한다.
/// 대상 언어는 DeepL식 코드(KO/EN-US/ZH-HANT)를 ISO 639-1(ko/en/zh)로 낮춰 전달한다.
/// </summary>
public sealed class LibreTranslateTranslator : ITranslator
{
    /// <summary>요청당 최대 텍스트 수(공개 인스턴스 부하 완화).</summary>
    private const int MaxTextsPerRequest = 20;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // api_key 없으면 필드 생략
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

            // q가 배열이면 translatedText도 배열로 반환된다.
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

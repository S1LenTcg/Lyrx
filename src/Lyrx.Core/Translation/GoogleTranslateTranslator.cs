using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>
/// Google Cloud Translation API v2 번역기(API 키 인증).
/// 원문 언어는 지정하지 않아 서버가 자동 감지한다(v2는 source 생략 시 자동 감지).
/// 대상 언어는 DeepL식 코드(KO/EN-US/ZH-HANT)를 Google 코드(ko/en/zh-TW)로 변환해 전달한다.
/// 응답의 translatedText는 format=text에서도 HTML 엔티티(&amp;#39; 등)로 escape되어 오므로 디코드한다.
/// </summary>
public sealed class GoogleTranslateTranslator : ITranslator
{
    private const string Endpoint = "https://translation.googleapis.com/language/translate/v2";

    /// <summary>요청당 최대 텍스트 수(API 상한 128, 여유를 둔다).</summary>
    private const int MaxTextsPerRequest = 64;

    /// <summary>요청당 최대 누적 문자 수(요청 크기 상한 여유). 초과 전에 끊어 보낸다.</summary>
    private const int MaxCharsPerRequest = 8000;

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public GoogleTranslateTranslator(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _http = http ?? LyricsHttp.Client;
    }

    public async Task<IReadOnlyList<string?>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLang, CancellationToken ct = default)
    {
        var target = ToGoogleLanguage(targetLang);
        var results = new string?[texts.Count];
        var url = $"{Endpoint}?key={Uri.EscapeDataString(_apiKey)}";

        for (var offset = 0; offset < texts.Count;)
        {
            var count = ChunkSize(texts, offset);
            var chunk = texts.Skip(offset).Take(count).ToList();

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new GoogleRequest(chunk, target, "text")),
            };

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<GoogleResponse>(ct).ConfigureAwait(false);

            if (body?.Data?.Translations is { } translations)
            {
                for (var i = 0; i < translations.Count && offset + i < results.Length; i++)
                    results[offset + i] = WebUtility.HtmlDecode(translations[i].TranslatedText);
            }

            offset += count;
        }

        return results;
    }

    /// <summary>
    /// DeepL식 target_lang → Google 언어 코드. 중국어만 간체/번체를 지역 코드로 구분하고,
    /// 나머지는 지역 접미사를 떼어 ISO 639-1로 내린다(EN-US → en, PT-BR → pt).
    /// </summary>
    public static string ToGoogleLanguage(string targetLang)
    {
        var upper = (targetLang ?? string.Empty).Trim().ToUpperInvariant();
        if (upper.Length == 0) return "en";
        return upper switch
        {
            "ZH" or "ZH-HANS" or "ZH-CN" => "zh-CN",
            "ZH-HANT" or "ZH-TW" => "zh-TW",
            "NB" or "NO" => "no",      // Google은 노르웨이어를 "no"로 받는다
            _ => upper.Split('-')[0].ToLowerInvariant(),
        };
    }

    /// <summary>이번 요청에 담을 텍스트 수(개수·문자 수 상한). 한 줄이 상한을 넘어도 최소 1개는 보낸다.</summary>
    private static int ChunkSize(IReadOnlyList<string> texts, int offset)
    {
        var count = 0;
        var chars = 0;
        while (offset + count < texts.Count && count < MaxTextsPerRequest)
        {
            var length = texts[offset + count].Length;
            if (count > 0 && chars + length > MaxCharsPerRequest) break;
            chars += length;
            count++;
        }
        return Math.Max(count, 1);
    }

    private sealed record GoogleRequest(
        [property: JsonPropertyName("q")] IReadOnlyList<string> Q,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("format")] string Format);

    private sealed record GoogleResponse(
        [property: JsonPropertyName("data")] GoogleData? Data);

    private sealed record GoogleData(
        [property: JsonPropertyName("translations")] List<GoogleTranslation>? Translations);

    private sealed record GoogleTranslation(
        [property: JsonPropertyName("translatedText")] string? TranslatedText,
        [property: JsonPropertyName("detectedSourceLanguage")] string? DetectedSourceLanguage);
}

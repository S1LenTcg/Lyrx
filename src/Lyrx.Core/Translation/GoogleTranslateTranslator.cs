using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>
/// Google Cloud Translation API v2 翻译器（API 密钥认证）。
/// 不指定原文语言，由服务器自动检测（v2 省略 source 时自动检测）。
/// 目标语言把 DeepL 风格的代码（KO/EN-US/ZH-HANT）换成 Google 代码（ko/en/zh-TW）后传入。
/// 响应中的 translatedText 即使在 format=text 下也会以 HTML 实体（&amp;#39; 等）转义返回，因此需要解码。
/// </summary>
public sealed class GoogleTranslateTranslator : ITranslator
{
    private const string Endpoint = "https://translation.googleapis.com/language/translate/v2";

    /// <summary>每次请求的最大文本条数（API 上限 128，这里留些余量）。</summary>
    private const int MaxTextsPerRequest = 64;

    /// <summary>每次请求的最大累计字符数（请求体量上限的余量）。超过之前就断开送出。</summary>
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
    /// DeepL 风格的 target_lang → Google 语言代码。只有中文用地区代码区分简/繁，
    /// 其余去掉地区后缀降到 ISO 639-1（EN-US → en，PT-BR → pt）。
    /// </summary>
    public static string ToGoogleLanguage(string targetLang)
    {
        var upper = (targetLang ?? string.Empty).Trim().ToUpperInvariant();
        if (upper.Length == 0) return "en";
        return upper switch
        {
            "ZH" or "ZH-HANS" or "ZH-CN" => "zh-CN",
            "ZH-HANT" or "ZH-TW" => "zh-TW",
            "NB" or "NO" => "no",      // Google 接受挪威语为 "no"
            _ => upper.Split('-')[0].ToLowerInvariant(),
        };
    }

    /// <summary>本次请求要装的文本条数（条数与字符数上限）。即使单行超过上限也至少发送 1 条。</summary>
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

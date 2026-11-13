using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Musebase.Core.Search;

namespace Musebase.Core.Translation;

/// <summary>responseStatus가 숫자(200) 또는 문자열("200")로 오는 것을 모두 문자열로 읽는다.</summary>
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
/// MyMemory 번역기(무키 무료·공식 API). 원문 언어는 <c>Autodetect</c>로 자동 감지한다.
/// 익명 하루 한도(약 5000단어)가 있어, 소진 시 예외를 던져 상위 폴백/힌트가 동작하게 한다.
/// 이메일(<see cref="TranslatorOptions.MyMemoryEmail"/>)을 주면 한도가 늘어난다(선택).
/// 대상 언어는 DeepL식 코드(KO/EN-US/ZH-HANT)를 ISO 639-1(ko/en/zh)로 낮춰 전달한다.
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

        // MyMemory는 단건 q만 지원 → 라인별 순차 요청(한도 완화 위해 병렬 지양). 캐시로 곡당 1회만 호출된다.
        for (var i = 0; i < texts.Count; i++)
        {
            var q = texts[i];
            if (string.IsNullOrWhiteSpace(q)) continue;

            var url = $"{Endpoint}?q={Uri.EscapeDataString(q)}&langpair={Uri.EscapeDataString($"Autodetect|{target}")}";
            if (_email is not null) url += $"&de={Uri.EscapeDataString(_email)}";

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<MyMemoryResponse>(ct).ConfigureAwait(false);

            // responseStatus는 문자열/숫자 모두 올 수 있다. 200이 아니면 실패.
            if (body?.ResponseStatus is { } status && status != "200")
                throw MakeFailure(status, body.ResponseDetails);

            var text = body?.ResponseData?.TranslatedText;
            // 한도 소진 등은 200으로 오되 대문자 경고문을 번역문 자리에 넣는다.
            if (text is null || IsWarning(text))
                throw MakeFailure("429", text);

            results[i] = text;
        }

        return results;
    }

    // 한도 초과 감지 → 429(RateLimit)로 분류되게 HttpRequestException으로 변환.
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

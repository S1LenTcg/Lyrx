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
        if (texts.Count == 0) return results;

        // 使用有限并发（最多 6 路）加速整首歌翻译；单行失败时不丢弃已成功译文，仅在全部失败时抛异常。
        using var gate = new SemaphoreSlim(6);
        Exception? firstFailure = null;
        var succeeded = 0;

        var tasks = new Task[texts.Count];
        for (var i = 0; i < texts.Count; i++)
        {
            var idx = i;
            var q = texts[idx];
            if (string.IsNullOrWhiteSpace(q))
            {
                tasks[idx] = Task.CompletedTask;
                continue;
            }

            tasks[idx] = Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var url = $"{Endpoint}?q={Uri.EscapeDataString(q)}&langpair={Uri.EscapeDataString($"Autodetect|{target}")}";
                    if (_email is not null) url += $"&de={Uri.EscapeDataString(_email)}";

                    using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var body = await response.Content.ReadFromJsonAsync<MyMemoryResponse>(ct).ConfigureAwait(false);

                    if (body?.ResponseStatus is { } status && status != "200")
                        throw MakeFailure(status, body.ResponseDetails);

                    var text = body?.ResponseData?.TranslatedText;
                    if (text is null || IsWarning(text))
                        throw MakeFailure("429", text);

                    results[idx] = text;
                    Interlocked.Increment(ref succeeded);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstFailure, ex, null);
                }
                finally
                {
                    gate.Release();
                }
            }, ct);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (succeeded == 0 && firstFailure is not null)
            throw firstFailure;

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

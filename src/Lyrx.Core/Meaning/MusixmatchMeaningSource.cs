using System.Text.Json;
using System.Text.RegularExpressions;

namespace Musebase.Core.Meaning;

/// <summary>
/// 抓取 Musixmatch 歌曲页面的 "Meaning"。<b>默认不开启</b> —
/// 必须在 <c>MUSEBASE_MEANING_SOURCES</c> 里写明 <c>musixmatch</c> 才会使用。
///
/// <b>这段文本不是人写的解说。</b>它位于页面 HTML 的 <c>__NEXT_DATA__</c> 中的 <c>lens</c>
/// 块里，同一块中还一并放着 <c>moods</c>·<c>themes</c>·内容分级 —
/// 那是机器分析歌词后得到的一组成果。也就是说把它当资料用，等于<b>把 LLM 写的文章再喂回 LLM 去概括</b>，
/// 因而无法追溯它依据了什么，不能与其他来源同等对待。
/// 所以把 "(AI 分析)" 写进名字，让它直接显现在出处标注里，并在提示词中也规定
/// 与其他资料冲突时以其他资料为准。背景与决策见 ADR-0007。
///
/// 地址只使用 <see cref="MusixmatchApi"/> 给出的 <c>track_share_url</c> — 按规则拼出的地址
/// 会悄悄跳到别的歌曲（参见那边的说明）。
/// </summary>
public sealed partial class MusixmatchMeaningSource : ISongMeaningSource
{
    /// <summary>短于此长度就不作为依据（与 Genius 相同的标准）。</summary>
    private const int MinLength = 40;

    private readonly MusixmatchApi _api;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public MusixmatchMeaningSource(MusixmatchApi api, HttpClient? http = null, int timeoutMs = 8000)
    {
        _api = api;
        _http = http ?? MeaningHttp.Client;
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
    }

    /// <summary>会原样写进出处标注 — 不能看起来像人写的解说。</summary>
    public string Name => "Musixmatch (AI 分析)";

    public async Task<MeaningSource?> FetchAsync(string title, string artist, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            // 不知道地址就在这里结束。不做猜测去抓取。
            var track = await _api.FindAsync(title, artist, cts.Token).ConfigureAwait(false);
            if (track?.ShareUrl is not { Length: > 0 } url) return null;

            var html = await _http.GetStringAsync(url, cts.Token).ConfigureAwait(false);
            var explanation = Explanation(html);
            if (explanation is null || explanation.Length < MinLength) return null;

            return new MeaningSource(Name, url, explanation);
        }
        catch (Exception)
        {
            return null; // 静默降级
        }
    }

    /// <summary>
    /// 从页面中取出含义段落（纯函数 — 测试对象）。
    ///
    /// 不把通往 <c>lens</c> 的路径固定下来，而是<b>递归查找</b> — Next.js 页面的数据结构会
    /// 与我们无关地变动，一旦把路径写死，变动的那一刻就会连异常都不抛地静默为空。
    /// </summary>
    internal static string? Explanation(string html)
    {
        var match = NextDataRegex().Match(html ?? "");
        if (!match.Success) return null;

        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            return FindLensMeaning(doc.RootElement)?.Trim();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindLensMeaning(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("lens")
                        && property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("meaning", out var meaning)
                        && meaning.ValueKind == JsonValueKind.Object
                        && meaning.TryGetProperty("explanation", out var text)
                        && text.ValueKind == JsonValueKind.String)
                        return text.GetString();

                    if (FindLensMeaning(property.Value) is { } found) return found;
                }
                return null;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (FindLensMeaning(item) is { } found) return found;
                return null;

            default:
                return null;
        }
    }

    [GeneratedRegex("""<script id="__NEXT_DATA__"[^>]*>(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex NextDataRegex();
}

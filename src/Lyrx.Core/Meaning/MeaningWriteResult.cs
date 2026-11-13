using System.Net;
using System.Text.Json;

namespace Musebase.Core.Meaning;

/// <summary>
/// 意义生成一次的结果。
///
/// 只有成功/失败两条路不够用，所以把失败分成三支。因为每一支的<b>是否保存</b>和
/// <b>要让人做什么</b>都不一样。
///
/// | 分支 | 例 | 保存 | 人要做的 |
/// |---|---|---|---|
/// | <see cref="Retryable"/> | 429, 5xx, 超时 | 否 | 稍后重试 |
/// | <see cref="NeedsSetup"/> | 402 余额, 401/403 密钥, 404 模型 | 否 | 处理付款、密钥、模型 |
/// | 其他(<see cref="Failed"/>) | 200 但正文为空 | 是 | 无(那首歌的问题) |
///
/// **引擎配置问题不能保存。** 回填会跳过已有行的歌，所以在余额耗尽的情况下
/// 继续跑，剩下的歌全都会被钉成「失败」。反过来只说「稍后重试」，人就会一直等一个
/// 自己不会好的问题 —— 实际上 402(余额)和 404(免费模型策略)就是这样被藏起来的。
/// 所以提供方给的原因(<see cref="Reason"/>)不丢弃，一路带到底。
/// </summary>
/// <param name="Text">生成的段落。为 null 即失败。</param>
/// <param name="Retryable">是否是过一段时间就会解的失败 —— 是的话就不保存。</param>
/// <param name="NeedsSetup">需要人去修引擎配置(付款、密钥、模型)的失败 —— 不保存。</param>
/// <param name="StatusCode">提供方给的 HTTP 状态。没收到响应时为 null。</param>
/// <param name="Reason">提供方给的错误语句(截短后的)。会原样显示在日志和管理界面上。</param>
public sealed record MeaningWriteResult(
    string? Text,
    bool Retryable,
    bool NeedsSetup = false,
    int? StatusCode = null,
    string? Reason = null)
{
    /// <summary>
    /// 实际使用的引擎、模型。在链式写入器(<see cref="CompositeMeaningWriter"/>)里，前面的引擎可能失败而
    /// 由后面的引擎来写，所以光看写入器的代表 id 不知道是谁写的。为空时就用写入器自己的值。
    /// </summary>
    public string? Engine { get; init; }

    /// <inheritdoc cref="Engine"/>
    public string? Model { get; init; }

    /// <summary>原因语句按这个长度截断 —— 必须能塞进一行日志、一行界面。</summary>
    public const int MaxReasonLength = 200;

    /// <summary>永久失败(响应为空等那首歌自身的问题) —— 保存下来然后跳过。</summary>
    public static readonly MeaningWriteResult Failed = new(null, false);

    /// <summary>暂时性失败(配额、服务器、网络、超时) —— 不保存。</summary>
    public static readonly MeaningWriteResult Transient = new(null, true);

    public static MeaningWriteResult Written(string text) => new(text, false);

    /// <summary>密钥为空 —— 连调用都无从发起，就是配置问题。</summary>
    public static MeaningWriteResult MissingKey() => new(null, false, NeedsSetup: true, Reason: "API 密钥为空");

    /// <summary>
    /// 收到了响应但仍然失败的情况。判断标准是「再调一次会不会不一样」和「是不是这首歌的问题」。
    /// - 429·5xx: 等一会儿就会恢复 → <see cref="Retryable"/>。
    /// - 其余的 4xx: 同样的配置下换哪首歌结果都一样 → <see cref="NeedsSetup"/>。
    ///   402(余额)要充值，401/403 要换密钥，404 要改模型名·账户策略才能解决。
    ///   400 也属于这里 —— 错误的模型 id 或密钥格式会以 400 返回(Gemini `API_KEY_INVALID`)。
    /// </summary>
    public static MeaningWriteResult FromStatus(HttpStatusCode code, string? body = null)
    {
        var status = (int)code;
        var reason = ReasonOf(body);
        return code == HttpStatusCode.TooManyRequests || status >= 500
            ? new(null, true, StatusCode: status, Reason: reason)
            : new(null, false, NeedsSetup: status is >= 400 and < 500, StatusCode: status, Reason: reason);
    }

    /// <summary>从响应正文里抽出人能读懂的一句话。OpenRouter·Gemini 都是 <c>{"error":{"message"}}</c>。</summary>
    public static string? ReasonOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        var text = body!;
        try
        {
            using var doc = JsonDocument.Parse(body!);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                    text = message.GetString() ?? body!;
                else if (error.ValueKind == JsonValueKind.String)
                    text = error.GetString() ?? body!;
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 就原样使用正文(代理错误页面等)。
        }

        var oneLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= MaxReasonLength ? oneLine : oneLine[..MaxReasonLength] + "…";
    }
}

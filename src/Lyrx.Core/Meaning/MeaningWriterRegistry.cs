namespace Musebase.Core.Meaning;

/// <summary>意义生成引擎的配置值。没有密钥的引擎不会被创建(null)。</summary>
public sealed record MeaningWriterOptions
{
    public string? GeminiApiKey { get; init; }
    public string? GeminiModel { get; init; }
    public string? OpenRouterApiKey { get; init; }
    public string? OpenRouterModel { get; init; }
    public HttpClient? Http { get; init; }
}

/// <summary>用于显示与选择的引擎描述符。</summary>
public sealed record MeaningWriterDescriptor(
    string Id,
    string Display,
    Func<MeaningWriterOptions, IMeaningWriter?> Factory);

/// <summary>
/// 意义生成引擎注册表。形态与 <see cref="Translation.TranslatorRegistry"/> 相同，因此
/// 设置界面与环境变量接线可直接复用。
///
/// 默认是 <c>none</c> —— 填入密钥前什么都不做，管理界面只显示外部链接。
/// </summary>
public static class MeaningWriterRegistry
{
    public const string None = "none";

    public static IReadOnlyList<MeaningWriterDescriptor> All { get; } = new MeaningWriterDescriptor[]
    {
        new("gemini", "Google Gemini (有 API 密钥 · 免费额度)",
            o => string.IsNullOrWhiteSpace(o.GeminiApiKey)
                ? null
                : new GeminiMeaningWriter(o.GeminiApiKey!, o.GeminiModel, o.Http)),

        new("openrouter", "OpenRouter (模型自选)",
            o => string.IsNullOrWhiteSpace(o.OpenRouterApiKey)
                ? null
                : new OpenRouterMeaningWriter(o.OpenRouterApiKey!, o.OpenRouterModel, o.Http)),
    };

    public static MeaningWriterDescriptor? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>创建选中的引擎。"none"/不支持/缺密钥则返回 null。</summary>
    public static IMeaningWriter? Build(string? id, MeaningWriterOptions options)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, None, StringComparison.OrdinalIgnoreCase))
            return null;
        return Find(id!)?.Factory(options);
    }
}

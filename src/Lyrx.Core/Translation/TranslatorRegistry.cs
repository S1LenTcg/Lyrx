namespace Musebase.Core.Translation;

/// <summary>创建翻译器所需的选项(按引擎各取所需)。</summary>
public sealed record TranslatorOptions(
    string? DeeplApiKey = null,
    string? LibreEndpoint = null,
    string? LibreApiKey = null,
    string? MyMemoryEmail = null,
    string? GoogleApiKey = null,
    string? OpenRouterApiKey = null,
    /// <summary>留空则用 <see cref="OpenRouterTranslator.DefaultModel"/>。例如：<c>anthropic/claude-opus-5</c>。</summary>
    string? OpenRouterModel = null,
    /// <summary>
    /// 翻译器要用的 <see cref="HttpClient"/>。留空则用各翻译器的默认值
    /// (<c>LyricsHttp.Client</c> —— 超时 15 秒)，因此 <b>应用行为保持不变</b>。
    ///
    /// 服务器端批量翻译这类 <b>允许耗时较长</b> 的调用，则传入更宽松的客户端。
    /// 15 秒是按播放中界面的响应性定的，会悄悄削掉 LLM 翻译的预算(OpenRouter 默认 60 秒、
    /// 最大 180 秒) —— 含义生成一侧出于同样的原因单独准备了专用客户端
    /// (<c>MeaningHttp</c>).
    /// </summary>
    HttpClient? Http = null,
    /// <summary>
    /// OpenRouter 模型能接受的输出 token 上限。留空则用翻译器默认值(8000)。
    /// 各模型不同(例如 Tencent HY-MT2 系列是 4096)，超限时响应会被截断，导致整批
    /// 被丢弃，所以知道具体值就填上 —— 批次大小会据此自动推导。
    /// </summary>
    int? OpenRouterMaxOutputTokens = null);

/// <summary>
/// 翻译引擎描述符。是否需要密钥、是否免费(供 UI/默认值判断)与创建工厂。
/// <c>RequiresApiKey</c>=没有密钥就不能工作(工厂返回 null)，
/// <c>AcceptsApiKey</c>=没有密钥也能用，填了就用(例如 LibreTranslate 公共实例)，
/// <c>ShortName</c>=用于"{engine} API 密钥"这类 UI 文案的短名称(留空则用 DisplayName)。
/// </summary>
public sealed record TranslatorDescriptor(
    string Id,
    string DisplayName,
    bool RequiresApiKey,
    bool IsFree,
    Func<TranslatorOptions, ITranslator?> Factory,
    bool AcceptsApiKey = false,
    string? ShortName = null)
{
    /// <summary>是否是需要暴露 API 密钥输入的引擎(必需或可选)。</summary>
    public bool UsesApiKey => RequiresApiKey || AcceptsApiKey;

    /// <summary>用于文案插入的短名称(不带括号说明的产品名)。</summary>
    public string Name => string.IsNullOrWhiteSpace(ShortName) ? DisplayName : ShortName!;
}

/// <summary>
/// 翻译引擎注册表。新引擎只要在 <see cref="All"/> 里加一行即可纳入。
/// 通过设置(<c>TranslationEngine</c>)选择，并用 <see cref="TranslatorOptions"/> 注入密钥/端点。
/// 密钥不足/不支持时工厂返回 null → 翻译不可用(仅使用提供方自带翻译)。
/// </summary>
public static class TranslatorRegistry
{
    /// <summary>翻译关闭的标识符。</summary>
    public const string None = "none";

    /// <summary>无需密钥的免费默认引擎(安装后即可使用)。LibreTranslate 公共实例已改为
    /// 必须付费密钥(2026)，因此用 MyMemory 作为默认的无密钥引擎。</summary>
    public const string DefaultFreeEngine = "mymemory";

    /// <summary>LibreTranslate 默认端点(可通过设置换成自托管实例)。</summary>
    public const string DefaultLibreEndpoint = "https://libretranslate.com";

    public static IReadOnlyList<TranslatorDescriptor> All { get; } = new TranslatorDescriptor[]
    {
        new("mymemory", "MyMemory (免费·无需密钥)", RequiresApiKey: false, IsFree: true,
            o => new MyMemoryTranslator(o.MyMemoryEmail, o.Http),
            ShortName: "MyMemory"),
        new("libretranslate", "LibreTranslate (自托管/密钥)", RequiresApiKey: false, IsFree: true,
            o => new LibreTranslateTranslator(
                string.IsNullOrWhiteSpace(o.LibreEndpoint) ? DefaultLibreEndpoint : o.LibreEndpoint!,
                o.LibreApiKey, o.Http),
            AcceptsApiKey: true, ShortName: "LibreTranslate"),
        new("deepl", "DeepL", RequiresApiKey: true, IsFree: false,
            o => string.IsNullOrWhiteSpace(o.DeeplApiKey) ? null : new DeeplTranslator(o.DeeplApiKey!, o.Http),
            ShortName: "DeepL"),
        new("google", "Google Cloud Translation (API 密钥)", RequiresApiKey: true, IsFree: false,
            o => string.IsNullOrWhiteSpace(o.GoogleApiKey)
                ? null
                : new GoogleTranslateTranslator(o.GoogleApiKey!, o.Http),
            ShortName: "Google Cloud Translation"),
        new("openrouter", "OpenRouter (LLM·自由选择模型)", RequiresApiKey: true, IsFree: false,
            o => string.IsNullOrWhiteSpace(o.OpenRouterApiKey)
                ? null
                : new OpenRouterTranslator(
                    o.OpenRouterApiKey!, o.OpenRouterModel, o.Http,
                    maxOutputTokens: o.OpenRouterMaxOutputTokens),
            ShortName: "OpenRouter"),
    };

    public static TranslatorDescriptor? Find(string id) =>
        All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>创建所选引擎的翻译器。"none"/不支持/密钥不足时为 null。</summary>
    public static ITranslator? Build(string id, TranslatorOptions options)
    {
        if (string.IsNullOrWhiteSpace(id) || string.Equals(id, None, StringComparison.OrdinalIgnoreCase))
            return null;
        return Find(id)?.Factory(options);
    }
}

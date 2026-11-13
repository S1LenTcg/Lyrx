namespace Musebase.Core.Meaning;

/// <summary>对一首歌生成含义的结果。</summary>
/// <param name="Status">`ok` | `no-source` | `insufficient` | `failed` | `retry` | `config`.</param>
/// <param name="Summary">生成出的目标语言段落。不是 `ok` 时为 null。</param>
/// <param name="Sources">作为依据的原文(用于标注来源、判断是否重新生成)。</param>
/// <param name="Detail">生成失败时提供方给出的原因(含状态码)。用于日志与管理界面。</param>
public sealed record SongMeaning(
    string Status,
    string? Summary,
    IReadOnlyList<MeaningSource> Sources,
    string? Engine,
    string? Model,
    string? Detail = null)
{
    public const string Ok = "ok";
    /// <summary>任何来源都没有资料 —— 没有调用 LLM。</summary>
    public const string NoSource = "no-source";
    /// <summary>资料存在，但生成永久性失败(密钥错误、响应为空)。</summary>
    public const string Failed = "failed";

    /// <summary>
    /// 暂时性失败(配额、服务器、网络) —— **不保存。** 若保存，配额恢复后
    /// 回填也会永远跳过这首歌。该状态是不写入数据库的值。
    /// </summary>
    public const string Retry = "retry";

    /// <summary>
    /// 引擎配置(结算余额、密钥、模型名称、账户策略)需要人工修正 —— **不保存。**
    /// 这不是歌曲本身的问题，若留下记录，回填会永远跳过本来正常的歌曲。与 `retry` 不同，
    /// 等待也解不开，因此必须在界面上如实显示原因。该状态是不写入数据库的值。
    /// </summary>
    public const string Config = "config";

    /// <summary>是否属于不保存的状态(<see cref="Retry"/>·<see cref="Config"/>)。</summary>
    public static bool IsUnsaved(string status) => status is Retry or Config;

    /// <summary>
    /// 找到了资料，但仅凭这些还不能说明歌曲的含义。段落保留(便于人工判断)，
    /// 但 **不计入"有意义"** —— 只要有字就算，统计会虚高，
    /// 应用里还会冒出"难以把握"这样的句子并当作歌曲解读。
    /// </summary>
    public const string Insufficient = "insufficient";

    public string? GeniusUrl =>
        Sources.FirstOrDefault(s => s.Name == "Genius")?.Url;
}

/// <summary>
/// 一次性完成：收集来源 → 生成摘要。
///
/// 设计上有两个核心点。
/// 1. **来源并行获取，失败直接忽略。** 一个挂掉，其余的照样补齐。
/// 2. **一条资料都没有时不调用 LLM。** 歌曲解读恰恰是最容易
///    被写成似是而非的领域，无依据地调用，模型就会编造。同时也是在浪费 token。
/// </summary>
public sealed class SongMeaningService
{
    private readonly IReadOnlyList<ISongMeaningSource> _sources;
    private readonly IMeaningWriter? _writer;

    public SongMeaningService(IReadOnlyList<ISongMeaningSource> sources, IMeaningWriter? writer)
    {
        _sources = sources;
        _writer = writer;
    }

    /// <summary>来源与引擎都未配置，则该功能处于关闭状态。</summary>
    public bool IsEnabled => _writer is not null && _sources.Count > 0;

    /// <summary>已启用的来源名称 —— 用于在界面上展示依据了哪些来源。</summary>
    public IReadOnlyList<string> SourceNames => _sources.Select(s => s.Name).ToList();

    public async Task<SongMeaning> BuildAsync(
        string title, string artist, string targetLang, CancellationToken ct = default)
    {
        var collected = await CollectAsync(title, artist, ct).ConfigureAwait(false);
        if (collected.Count == 0)
            return new SongMeaning(SongMeaning.NoSource, null, collected, _writer?.EngineId, _writer?.Model);

        if (_writer is null)
            return new SongMeaning(SongMeaning.Failed, null, collected, null, null);

        var written = await _writer.WriteAsync(title, artist, collected, targetLang, ct).ConfigureAwait(false);
        if (written.Text is not null)
        {
            // "资料不足"的答复也是正常行为，但不算有含义 —— 分开记录。
            var verdict = MeaningVerdict.IsInsufficient(written.Text)
                ? SongMeaning.Insufficient
                : SongMeaning.Ok;
            return new SongMeaning(
                verdict, MeaningVerdict.Strip(written.Text), collected,
                written.Engine ?? _writer.EngineId, written.Model ?? _writer.Model);
        }

        // 配额、网络这类靠时间能解决的失败与配置问题，不固化为 `failed`。
        var status = written.NeedsSetup ? SongMeaning.Config
            : written.Retryable ? SongMeaning.Retry
            : SongMeaning.Failed;
        return new SongMeaning(status, null, collected,
            written.Engine ?? _writer.EngineId, written.Model ?? _writer.Model, DetailOf(written));
    }

    /// <summary>把状态码与原因合成一行，如 "HTTP 402 · 余额不足"。</summary>
    private static string? DetailOf(MeaningWriteResult written) =>
        (written.StatusCode, written.Reason) switch
        {
            ({ } code, { } reason) => $"HTTP {code} · {reason}",
            ({ } code, null) => $"HTTP {code}",
            (null, { } reason) => reason,
            _ => null,
        };

    /// <summary>同时调用所有来源，只收集成功的结果(保持注册表注册顺序)。</summary>
    public async Task<IReadOnlyList<MeaningSource>> CollectAsync(
        string title, string artist, CancellationToken ct = default)
    {
        var tasks = _sources.Select(s => s.FetchAsync(title, artist, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToList();
    }
}

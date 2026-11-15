namespace Musebase.Core.Meaning;

/// <summary>
/// 从单个来源取得的歌曲背景原文。<paramref name="Text"/> 多为英文散文，
/// 因此不原样展示，而由 <see cref="IMeaningWriter"/> 概括成中文。
/// </summary>
/// <param name="Name">用于显示的来源名称（有标注出处的义务 —— 会原样渲染到界面上）。</param>
/// <param name="Url">供人核对原文的地址。没有则为 null。</param>
/// <param name="Text">抓取到的正文。</param>
public sealed record MeaningSource(string Name, string? Url, string Text);

/// <summary>
/// 从单一位置获取歌曲背景原文的契约。
///
/// **失败不抛异常，而是返回 null** —— 未配置密钥、超时、搜索失败、被拦截，结果全都一样
/// （与 <see cref="Search.HttpRemoteLyricsCache"/> 的静默降级是同一原则）。即使一个来源失效，
/// 其余来源也会补上；若全部为空，调用方根本不会去调用 LLM。
/// </summary>
public interface ISongMeaningSource
{
    /// <summary>用于显示与记录的来源 id（例如 "genius"）。</summary>
    string Name { get; }

    Task<MeaningSource?> FetchAsync(string title, string artist, CancellationToken ct = default);
}

using System.Text;

namespace Musebase.Core.Meaning;

/// <summary>
/// 读取收集到的英文原文，用目标语言写出"这首歌是关于什么的"。
///
/// 这不是翻译而是**摘要**，所以用 <see cref="Translation.ITranslator"/> 不行 —— 把很长的英文
/// bio 塞给 DeepL 只会得到一篇很长的韩语文档，成不了"含义"。实现是纯 HTTP + JSON，
/// 引擎通过 <see cref="MeaningWriterRegistry"/> 替换（与翻译引擎结构相同）。
///
/// 失败不是异常，而是 <see cref="MeaningWriteResult"/> —— 含义是附加功能，不能因为没有它
/// 就不显示歌词。只是"暂时性失败"要单独区分返回（见那边的说明）。
/// </summary>
public interface IMeaningWriter
{
    /// <summary>注册表 id（例如 "gemini"）。记录是用哪个引擎生成的。</summary>
    string EngineId { get; }

    /// <summary>实际调用的模型名称（用于判断是否重新生成、以及记录）。</summary>
    string Model { get; }

    Task<MeaningWriteResult> WriteAsync(
        string title, string artist, IReadOnlyList<MeaningSource> sources,
        string targetLang, CancellationToken ct = default);
}

/// <summary>
/// 无论哪个引擎都使用同一份提示词 —— 为了让换模型时结果的性质不漂移，
/// 也为了能把两个引擎的输出并排比较。
/// </summary>
public static class MeaningPrompt
{
    /// <summary>无论原文多长，最多只放这么长（防止 token 爆炸）。</summary>
    public const int MaxSourceChars = 6000;

    /// <summary>
    /// 输出上限。3~5 句就够了，即使放宽也差不多是这个量。
    /// **必须随请求一起发送** —— 不发送上限时，供方会按模型最大值去预留，
    /// 余额较少的账号会被 402 拒掉（OpenRouter 实测）。同时也是防止费用爆炸。
    /// </summary>
    public const int MaxOutputTokens = 1200;

    /// <summary>
    /// 最后一句是这个提示词的核心 —— 让模型在资料不足时不去编造，
    /// 而是写出"资料不足"。歌曲解说恰恰是最容易编出像样内容的地方。
    /// </summary>
    public static string Build(
        string title, string artist, IReadOnlyList<MeaningSource> sources, string targetLang)
    {
        var language = LanguageName(targetLang);
        var sb = new StringBuilder();
        sb.Append("以下是从多个网页资料中收集到的关于一首歌的说明。\n\n");
        sb.Append($"歌曲: {title}\n艺术家: {artist}\n\n");

        var budget = MaxSourceChars;
        foreach (var source in sources)
        {
            if (budget <= 0) break;
            var text = source.Text.Length > budget ? source.Text[..budget] : source.Text;
            budget -= text.Length;
            sb.Append($"[{source.Name}]\n{text}\n\n");
        }

        sb.Append($"""
            仅以上述资料为依据，用 {language} 写 3~5 句话说明这首歌是关于什么的。

            - 以创作背景、歌词涉及的主题、已有的解读为中心来写。
            - 资料中没有的内容绝不编造。不做猜测。
            - 如果资料不足、无法说明这首歌是关于什么的，就在第一行准确写出
              [자료부족]，然后后面只用一句话写原因。不要硬凑。
            - 不要放入榜单成绩、获奖经历这类与歌曲含义无关的事实。
            - 如果资料里混有引语或个人感想（"我加入乐队以后……"、"真的很棒"），
              不要把它当成客观叙述照搬。只写这首歌是关于什么的。
            - 来源名称带 "AI 分析"的资料是机器对歌词的解读，事实依据较弱。
              与其他资料冲突时以其他资料为准；只有它时不要下断言。
            - 不要前言，只写正文。
            """);
        return sb.ToString();
    }

    private static string LanguageName(string code) => code.ToLowerInvariant() switch
    {
        "ko" => "韩语",
        "ja" => "日语",
        "en" => "英语",
        "zh" or "zh-hans" or "zh-hant" => "中文",
        _ => code,
    };
}

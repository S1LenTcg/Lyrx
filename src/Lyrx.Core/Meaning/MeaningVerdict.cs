using System.Text.RegularExpressions;

namespace Musebase.Core.Meaning;

/// <summary>
/// 判断生成的段落到底是<b>真的在讲这首歌的含义</b>，还是一句"资料不足，无从言说"的自白。
///
/// 提示词为了阻止没有依据的编造，会要求"资料不足就直说不足"，所以这类回答属于正常行为的一种。
/// 问题在于，<b>只要有字就记成"有意义"</b>，统计就会虚高，应用里也会把
/// "难以判断"的句子当成歌曲解读呈现出来。因此单独拆出一个状态。
///
/// 判定分两层。
/// ① <b>标记</b> —— 提示词要求这种情况在第一行写 <c>[자료부족]</c>（最可靠）。
/// ② <b>措辞</b> —— 也有不加标记的模型，因此同时看针对资料本身的说法（"仅凭资料"、
///    "难以判断"）。真正在讲歌的句子不会用这些表达。
/// </summary>
public static partial class MeaningVerdict
{
    /// <summary>提示词要求的标记。从响应里剥除后再存储。</summary>
    public const string Marker = "[자료부족]";

    /// <summary>剥掉标记之后的正文（没有标记则保持原文）。</summary>
    public static string Strip(string text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.StartsWith(Marker, StringComparison.Ordinal)
            ? trimmed[Marker.Length..].Trim()
            : trimmed;
    }

    /// <summary>这一段是否为"资料不足"的自白。</summary>
    public static bool IsInsufficient(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return true;
        if (trimmed.StartsWith(Marker, StringComparison.Ordinal)) return true;
        return ExcuseRegex().IsMatch(trimmed);
    }

    /// <summary>
    /// 只挑出针对资料本身的说法。为了不误伤"这首歌讲的是失去"这类句子，
    /// 只有在<b>以资料·信息为主语的表达</b>和<b>无法言说的叙述</b>同时出现时才判定。
    /// </summary>
    [GeneratedRegex(
        @"(자료|정보)[^.!?\n]{0,40}(부족|없|담고 있지 않|포함되어 있지 않)" +
        @"|(자료|정보)만으로는" +
        @"|(파악|설명|말)하기\s*(가\s*)?(어렵|힘들)" +
        @"|알\s*수\s*없(다|습니다)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExcuseRegex();
}

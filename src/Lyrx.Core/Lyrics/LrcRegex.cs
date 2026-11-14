using System.Text.RegularExpressions;

namespace Musebase.Core;

/// <summary>
/// LRC 解析用正则表达式。LyricsKit 的 RegexPattern.swift 移植版。
/// </summary>
internal static partial class LrcRegex
{
    /// <summary>[mm:ss.xx] 时间标签（允许正负号）</summary>
    [GeneratedRegex(@"\[([-+]?\d+):(\d+(?:\.\d+)?)\]")]
    public static partial Regex TimeTag();

    /// <summary>[key:value] ID 标签行（不含时间标签行）</summary>
    [GeneratedRegex(@"^(?!\[[+-]?\d+:\d+(?:\.\d+)?\])\[(.+?):(.+)\]$", RegexOptions.Multiline)]
    public static partial Regex Id3Tag();

    /// <summary>歌词行：至少 1 个时间标签 + 正文 + 可选的 【翻译】</summary>
    [GeneratedRegex(@"^((?:\[[+-]?\d+:\d+(?:\.\d+)?\])+)(?!\[)([^【\n\r]*)(?:【(.*)】)?", RegexOptions.Multiline)]
    public static partial Regex LyricsLine();

    /// <summary>附加行：时间标签 + [标签]内容（例：[00:01.00][tr:zh]译文）</summary>
    [GeneratedRegex(@"^((?:\[[+-]?\d+:\d+(?:\.\d+)?\])+)\[(.+?)\](.*)", RegexOptions.Multiline)]
    public static partial Regex LineAttachment();

    /// <summary>(mm:)ss.xx 形式的时长写法</summary>
    [GeneratedRegex(@"^\s*(?:(\d+):)?(\d+(?:\.\d+)?)\s*$")]
    public static partial Regex Base60Time();

    /// <summary>内联时间标签 &lt;msec,index&gt;</summary>
    [GeneratedRegex(@"<(\d+,\d+)>")]
    public static partial Regex InlineTimeTag();

    /// <summary>内联持续时间 &lt;msec&gt;</summary>
    [GeneratedRegex(@"<(\d+)>")]
    public static partial Regex InlineDuration();

    /// <summary>KRC/QRC 歌词行：[startMs,durationMs]正文</summary>
    [GeneratedRegex(@"^\[(\d+),(\d+)\](.*)", RegexOptions.Multiline)]
    public static partial Regex KrcLine();

    /// <summary>Kugou 逐字内联标签 &lt;relStartMs,durationMs,0&gt;fragment</summary>
    [GeneratedRegex(@"<(\d+),(\d+),0>([^<]*)")]
    public static partial Regex KugouInlineTag();

    /// <summary>QQ 逐字内联标签 fragment(absStartMs,durationMs) — fragment 为非贪婪匹配</summary>
    [GeneratedRegex(@"(.*?)\((\d+),(\d+)\)")]
    public static partial Regex QqInlineTag();

    /// <summary>NetEase yrc 内联标签 (absStartMs,durationMs,0)fragment</summary>
    [GeneratedRegex(@"\((\d+),(\d+),0\)([^(]*)")]
    public static partial Regex NetEaseYrcInlineTag();

    /// <summary>NetEase klyric 内联标签 (0,durationMs)fragment[(0,1) ] — 末尾为可选的空格插入</summary>
    [GeneratedRegex(@"\(0,(\d+)\)([^(]+)(\(0,1\) )?")]
    public static partial Regex NetEaseKLyricInlineTag();

    /// <summary>解析时间标签字符串中的秒单位位置列表。</summary>
    public static List<double> ResolveTimeTags(string str)
    {
        var result = new List<double>();
        foreach (Match m in TimeTag().Matches(str))
        {
            var min = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var sec = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            result.Add(min * 60 + sec);
        }
        return result;
    }
}

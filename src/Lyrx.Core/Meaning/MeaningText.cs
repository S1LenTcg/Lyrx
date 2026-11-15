using System.Net;
using System.Text.RegularExpressions;

namespace Musebase.Core.Meaning;

/// <summary>为比较标题·艺术家而做的归一化。让各来源实现使用同一套标准。</summary>
public static partial class MeaningText
{
    /// <summary>
    /// 只保留小写字母 + 英文字母数字/韩文(去掉括号·标点·空白)。
    ///
    /// 在此之前必须先处理两件事 —— 实测表明这两件都会让整首歌被漏掉。
    ///
    /// ① <b>HTML 标签与实体。</b>维基百科的搜索片段会原样带上 <c>&lt;span class="searchmatch"&gt;</c>和
    ///    <c>&amp;amp;</c>。不清理掉的话 <c>Belle &amp;amp; Sebastian</c> 会变成
    ///    <c>belleampsebastian</c>("amp" 混进了字符里)，跟什么都匹配不上。
    /// ② <b>&amp; 和 and 是同一个意思。</b>我们收到的写法是 "Belle and Sebastian"，而文档里
    ///    常写成 "Belle &amp; Sebastian"，这种分歧很常见。
    /// </summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var text = TagRegex().Replace(s, " ");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("&", " and ", StringComparison.Ordinal);

        Span<char> buffer = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var n = 0;
        foreach (var c in text)
            if (char.IsLetterOrDigit(c)) buffer[n++] = char.ToLowerInvariant(c);
        return new string(buffer[..n]);
    }

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();
}

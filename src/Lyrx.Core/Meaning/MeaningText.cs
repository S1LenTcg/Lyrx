using System.Net;
using System.Text.RegularExpressions;

namespace Musebase.Core.Meaning;

/// <summary>제목·아티스트를 견주기 위한 정규화. 소스 구현들이 같은 기준을 쓰게 한다.</summary>
public static partial class MeaningText
{
    /// <summary>
    /// 소문자 + 영숫자/한글만 남긴다(괄호·구두점·공백 제거).
    ///
    /// 그 전에 두 가지를 반드시 처리한다 — 실측으로 둘 다 곡을 통째로 놓치게 만들었다.
    ///
    /// ① <b>HTML 태그와 엔티티.</b> 위키피디아 검색 스니펫은 <c>&lt;span class="searchmatch"&gt;</c>와
    ///    <c>&amp;amp;</c>를 그대로 담아 온다. 지우지 않으면 <c>Belle &amp;amp; Sebastian</c>이
    ///    <c>belleampsebastian</c>이 되어("amp"가 글자로 섞인다) 무엇과도 맞지 않는다.
    /// ② <b>&amp;와 and는 같은 말이다.</b> 우리가 받은 표기가 "Belle and Sebastian"인데 문서는
    ///    "Belle &amp; Sebastian"으로 적는 식으로 흔히 갈린다.
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

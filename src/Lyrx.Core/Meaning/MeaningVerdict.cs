using System.Text.RegularExpressions;

namespace Musebase.Core.Meaning;

/// <summary>
/// 생성된 문단이 <b>정말 곡의 의미인지</b>, 아니면 "자료가 부족해 말할 수 없다"는 고백인지 가른다.
///
/// 프롬프트가 근거 없는 창작을 막으려고 "부족하면 부족하다고 쓰라"고 시키므로, 이 답은 정상 동작의
/// 일부다. 문제는 <b>글자가 있다는 이유만으로 "의미 있음"으로 세면</b> 통계가 부풀고, 앱에는
/// "파악하기 어렵다"는 문장이 곡 해설이라며 뜬다는 것이다. 별도 상태로 갈라 둔다.
///
/// 판정은 두 겹이다.
/// ① <b>표식</b> — 프롬프트가 이 경우 첫 줄에 <c>[자료부족]</c>을 쓰게 한다(가장 확실하다).
/// ② <b>문구</b> — 표식을 안 붙이는 모델도 있어, 자료 자체를 두고 하는 말("자료만으로는",
///    "파악하기 어렵다")을 함께 본다. 곡 이야기를 하는 문장은 이런 표현을 쓰지 않는다.
/// </summary>
public static partial class MeaningVerdict
{
    /// <summary>프롬프트가 요구하는 표식. 응답에서는 지우고 저장한다.</summary>
    public const string Marker = "[자료부족]";

    /// <summary>표식을 떼어 낸 본문(없으면 원문 그대로).</summary>
    public static string Strip(string text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.StartsWith(Marker, StringComparison.Ordinal)
            ? trimmed[Marker.Length..].Trim()
            : trimmed;
    }

    /// <summary>이 문단이 "자료가 부족하다"는 고백인가.</summary>
    public static bool IsInsufficient(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return true;
        if (trimmed.StartsWith(Marker, StringComparison.Ordinal)) return true;
        return ExcuseRegex().IsMatch(trimmed);
    }

    /// <summary>
    /// 자료 자체를 두고 하는 말만 고른다. "이 곡은 상실을 다룬다" 같은 문장은 걸리지 않도록
    /// <b>자료·정보를 주어로 삼는 표현</b>과 <b>말할 수 없다는 서술</b>이 함께 있을 때만 본다.
    /// </summary>
    [GeneratedRegex(
        @"(자료|정보)[^.!?\n]{0,40}(부족|없|담고 있지 않|포함되어 있지 않)" +
        @"|(자료|정보)만으로는" +
        @"|(파악|설명|말)하기\s*(가\s*)?(어렵|힘들)" +
        @"|알\s*수\s*없(다|습니다)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ExcuseRegex();
}

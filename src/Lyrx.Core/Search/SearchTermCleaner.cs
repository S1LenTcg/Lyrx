using System.Text.RegularExpressions;

namespace Musebase.Core.Search;

/// <summary>
/// 스트리밍 메타데이터의 잡음(피처링/리마스터/라이브/버전 표기 등)을 제거해
/// 추가 검색어 변형을 만든다. LyricsKit의 LyricsSearchRequestPlugin 취지(검색 확장)를
/// 트랙 메타 정제로 구현한 것으로, 정제 변형은 원본 검색어와 함께 검색되어
/// 제공자 매칭 실패를 줄인다. 정제가 원본과 같으면 아무것도 반환하지 않는다.
/// </summary>
public static partial class SearchTermCleaner
{
    /// <summary>원본과 다른 정제 변형 목록(없으면 빈 목록).</summary>
    public static IReadOnlyList<SearchTerm> Variants(SearchTerm term)
    {
        var result = new List<SearchTerm>();

        if (term.IsKeyword)
        {
            var cleaned = CleanTitle(term.Keyword!);
            if (cleaned.Length > 0 && !cleaned.Equals(term.Keyword, StringComparison.OrdinalIgnoreCase))
                result.Add(new SearchTerm(cleaned));
        }
        else
        {
            var title = CleanTitle(term.Title!);
            var artist = CleanArtist(term.Artist!);
            var changed = !title.Equals(term.Title, StringComparison.OrdinalIgnoreCase)
                       || !artist.Equals(term.Artist, StringComparison.OrdinalIgnoreCase);
            if (changed && title.Length > 0)
                result.Add(new SearchTerm(title, artist));
        }

        return result;
    }

    /// <summary>제목 정제: 괄호/대시 잡음 + 피처링 제거.</summary>
    internal static string CleanTitle(string title)
    {
        var s = BracketFeatRegex().Replace(title, " ");
        s = BracketNoiseRegex().Replace(s, " ");
        s = DashNoiseRegex().Replace(s, " ");
        s = InlineFeatRegex().Replace(s, " ");
        return Collapse(s);
    }

    /// <summary>
    /// 아티스트 정제: 피처링과 스트리밍 앱이 붙인 문맥 꼬리표를 제거한다
    /// (구분자 분할은 다인 아티스트 오손 우려로 하지 않음).
    /// </summary>
    internal static string CleanArtist(string artist)
    {
        var s = BracketFeatRegex().Replace(artist, " ");
        s = InlineFeatRegex().Replace(s, " ");
        s = BulletSuffixRegex().Replace(s, " ");
        return Collapse(s);
    }

    private static string Collapse(string s) =>
        WhitespaceRegex().Replace(s, " ").Trim().Trim('-', '–', '—').Trim();

    // (feat. X) / [ft X] / （featuring …） / (with …) — 괄호 안은 with도 안전
    [GeneratedRegex(@"[\(\[（]\s*(?:feat|ft|featuring|with)\b\.?[^\)\]）]*[\)\]）]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketFeatRegex();

    // 괄호 안 잡음 키워드
    [GeneratedRegex(@"[\(\[（][^\)\]）]*\b(?:remaster(?:ed)?|re-?master|live|radio\s*edit|single\s*version|album\s*version|mono|stereo|deluxe|bonus\s*track|acoustic|demo|instrumental|anniversary|remix|edit|version)\b[^\)\]）]*[\)\]）]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketNoiseRegex();

    // " - Remastered 2011" 류 트레일링 대시 잡음(공백으로 감싼 대시만 대상 → "Spider-Man" 보존)
    [GeneratedRegex(@"\s[-–—]\s[^-–—]*\b(?:remaster(?:ed)?|re-?master|live|radio\s*edit|single\s*version|album\s*version|mono|stereo|deluxe|bonus\s*track|acoustic|demo|instrumental|anniversary|remix|edit|version)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashNoiseRegex();

    // 괄호 없는 트레일링 "feat. ..."(with는 정상 제목 오손 우려로 제외)
    [GeneratedRegex(@"\s(?:feat|ft|featuring)\b\.?\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex InlineFeatRegex();

    // " • 스마트셔플 추천" / " • 회원님을 위한 추천 목록" — 스트리밍 앱이 아티스트 뒤에 붙이는
    // 문맥 표기(실측: Spotify Android). 공백으로 감싼 불릿만 대상으로 해 이름 안의 기호는 남긴다.
    [GeneratedRegex(@"\s[•·]\s.*$")]
    private static partial Regex BulletSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

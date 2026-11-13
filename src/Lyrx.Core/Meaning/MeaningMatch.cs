namespace Musebase.Core.Meaning;

/// <summary>
/// 검색 결과가 정말 그 곡인지 판정한다.
///
/// Genius도 Musixmatch도 **무엇을 넣든 무언가를 돌려준다.** 실측에서 음악이 아닌 유튜브 제목
/// ("해외에서 화제라는 한국의 지하철 문화")으로 검색했더니 전혀 무관한 곡이 첫 히트로 나왔고,
/// 확인 없이 받았으면 그 곡의 해설이 이 트랙의 "의미"로 붙었을 것이다.
/// <b>엉뚱한 근거는 자료가 없는 것보다 나쁘다</b> — 그럴듯하고 완전히 틀린 글이 만들어지기 때문이다.
///
/// 그래서 검색 기반 소스는 전부 이 하나의 기준을 통과해야 한다.
/// </summary>
public static class MeaningMatch
{
    /// <summary>
    /// 제목 일치는 필수, 아티스트는 아는 경우에만 확인을 요구한다.
    /// 제목은 어느 쪽이 담아도 인정한다 — "(Remix)"·"(Live)" 같은 꼬리표가 흔하다.
    /// </summary>
    public static bool IsSameSong(string? hitTitle, string? hitArtists, string title, string artist)
    {
        var wanted = MeaningText.Normalize(title);
        var got = MeaningText.Normalize(hitTitle ?? "");
        if (wanted.Length == 0 || got.Length == 0) return false;

        if (!got.Contains(wanted, StringComparison.Ordinal)
            && !wanted.Contains(got, StringComparison.Ordinal)) return false;

        var names = ArtistNames.All(artist)
            .Select(MeaningText.Normalize)
            .Where(n => n.Length >= 3)
            .ToList();
        if (names.Count == 0) return true; // 아티스트를 모르면 제목만으로 받아들인다

        var haystack = MeaningText.Normalize(hitArtists ?? "");
        return names.Any(n => haystack.Contains(n, StringComparison.Ordinal));
    }
}

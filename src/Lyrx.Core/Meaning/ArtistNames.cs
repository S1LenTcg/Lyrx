namespace Musebase.Core.Meaning;

/// <summary>
/// 아티스트 표기에서 **비교에 쓸 이름 후보들**을 뽑는다.
///
/// 재생 메타데이터의 아티스트 필드는 생각보다 지저분해서, 통째로 비교하면 어떤 문서와도
/// 맞지 않는다. 실측으로 두 가지가 걸렸다.
///
/// ① **앨범이 꼬리표로 붙어 온다** — <c>"harry styles — harry's house"</c>.
/// ② **합작곡은 구분자로 이어 온다** — <c>"Lady Gaga/Bradley Cooper"</c>. 그런데 위키피디아
///    문서 제목은 <c>"Shallow (Lady Gaga and Bradley Cooper song)"</c>라서, 구두점을 지우고
///    통째로 포함 검사를 하면 가운데 "and" 때문에 절대 일치하지 않는다.
///    **한 명만 확인돼도** 동명이곡을 거르는 목적은 달성된다.
///
/// 구분자는 **앞뒤 공백이 있는 것만** 본다 — 이름 자체에 기호가 든 경우
/// (<c>Jay-Z</c>, <c>AC/DC</c>)를 자르면 안 되기 때문이다. 그래도 <c>AC/DC</c>처럼 쪼개지는
/// 이름이 남는데, 호출자가 "쓸 만한 이름이 없으면 원본 전체로 되돌리는" 방식으로 받아 준다.
/// </summary>
public static class ArtistNames
{
    /// <summary>아티스트 뒤에 붙는 앨범 꼬리표 구분자(공백 포함).</summary>
    private static readonly string[] AlbumSeparators = [" — ", " – ", " • ", " · "];

    /// <summary>여러 아티스트를 잇는 구분자. 슬래시만 공백 없이도 흔해 예외로 둔다.</summary>
    private static readonly string[] ArtistSeparators =
        ["/", " & ", ", ", " feat. ", " feat ", " featuring ", " ft. ", " ft ", " with ", " x "];

    /// <summary>" — 앨범명" 같은 꼬리표를 떼어 낸다.</summary>
    public static string StripAlbumSuffix(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var value = artist.Trim();
        foreach (var separator in AlbumSeparators)
        {
            var at = value.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0) value = value[..at].Trim();
        }
        return value;
    }

    /// <summary>앨범 꼬리표를 떼고 여러 아티스트로 나눈 이름들(등장 순서 유지).</summary>
    public static IReadOnlyList<string> All(string artist)
    {
        var value = StripAlbumSuffix(artist);
        if (value.Length == 0) return [];

        var parts = value.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries
                                               | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? [value] : parts;
    }

    /// <summary>대표 이름 하나 — 검색어를 만들 때 쓴다(꼬리표·공동 아티스트는 잡음이다).</summary>
    public static string Primary(string artist)
    {
        var all = All(artist);
        return all.Count > 0 ? all[0] : artist.Trim();
    }
}

namespace Musebase.Core.Search;

/// <summary>
/// 가사 검색 요청. LyricsKit의 LyricsSearchRequest 포팅(플러그인 파생은 제외).
/// </summary>
public sealed record LyricsSearchRequest
{
    /// <summary>키워드 검색 또는 제목/아티스트 검색</summary>
    public required SearchTerm Term { get; init; }

    /// <summary>곡 길이(초). 랭킹 시 길이 매칭에 사용. 모르면 0.</summary>
    public double Duration { get; init; }

    /// <summary>제공자별 후보 최대 취득 수</summary>
    public int Limit { get; init; } = 6;

    public static LyricsSearchRequest ByInfo(string title, string artist, double duration = 0, int limit = 6) =>
        new() { Term = new SearchTerm(title, artist), Duration = duration, Limit = limit };

    public static LyricsSearchRequest ByKeyword(string keyword, double duration = 0, int limit = 6) =>
        new() { Term = new SearchTerm(keyword), Duration = duration, Limit = limit };
}

/// <summary>keyword 또는 (title, artist) 검색어</summary>
public sealed record SearchTerm
{
    public string? Keyword { get; }
    public string? Title { get; }
    public string? Artist { get; }

    public bool IsKeyword => Keyword is not null;

    public SearchTerm(string keyword) => Keyword = keyword;

    public SearchTerm(string title, string artist)
    {
        Title = title;
        Artist = artist;
    }

    /// <summary>통합 검색어 문자열 ("제목 아티스트" 또는 키워드)</summary>
    public override string ToString() => Keyword ?? $"{Title} {Artist}";

    public string TitleOnly => Keyword ?? Title!;
}

namespace Musebase.Core.Search;

/// <summary>
/// 歌词搜索请求。LyricsKit 的 LyricsSearchRequest 移植（不含插件派生）。
/// </summary>
public sealed record LyricsSearchRequest
{
    /// <summary>关键词搜索或标题/艺术家搜索</summary>
    public required SearchTerm Term { get; init; }

    /// <summary>歌曲时长（秒）。排序时用于时长匹配。未知则为 0。</summary>
    public double Duration { get; init; }

    /// <summary>每个提供方最多获取的候选数</summary>
    public int Limit { get; init; } = 6;

    public static LyricsSearchRequest ByInfo(string title, string artist, double duration = 0, int limit = 6) =>
        new() { Term = new SearchTerm(title, artist), Duration = duration, Limit = limit };

    public static LyricsSearchRequest ByKeyword(string keyword, double duration = 0, int limit = 6) =>
        new() { Term = new SearchTerm(keyword), Duration = duration, Limit = limit };
}

/// <summary>keyword 或 (title, artist) 搜索词</summary>
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

    /// <summary>合并后的搜索词字符串（"标题 艺术家" 或关键词）</summary>
    public override string ToString() => Keyword ?? $"{Title} {Artist}";

    public string TitleOnly => Keyword ?? Title!;
}

using System.Text.RegularExpressions;

namespace Musebase.Core.Search;

/// <summary>
/// 去除流媒体元数据里的噪声（featuring/重制/现场/版本标注等），
/// 生成额外的搜索词变体。把 LyricsKit 的 LyricsSearchRequestPlugin 的用意（搜索扩展）
/// 落在曲目元数据清洗上实现，清洗出的变体会与原搜索词一起检索，
/// 以减少提供方匹配失败。清洗结果与原文相同时不返回任何内容。
/// </summary>
public static partial class SearchTermCleaner
{
    /// <summary>与原文不同的清洗变体列表（没有则为空列表）。</summary>
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

    /// <summary>标题清洗：括号/破折号噪声 + featuring 去除。</summary>
    internal static string CleanTitle(string title)
    {
        var s = BracketFeatRegex().Replace(title, " ");
        s = BracketNoiseRegex().Replace(s, " ");
        s = DashNoiseRegex().Replace(s, " ");
        s = InlineFeatRegex().Replace(s, " ");
        return Collapse(s);
    }

    /// <summary>
    /// 艺人清洗：去除 featuring 以及流媒体应用挂上的上下文尾标
    /// （因担心破坏多位艺人，不做分隔符切分）。
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

    // (feat. X) / [ft X] / （featuring …） / (with …) — 括号内的 with 也算安全
    [GeneratedRegex(@"[\(\[（]\s*(?:feat|ft|featuring|with)\b\.?[^\)\]）]*[\)\]）]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketFeatRegex();

    // 括号内的噪声关键词
    [GeneratedRegex(@"[\(\[（][^\)\]）]*\b(?:remaster(?:ed)?|re-?master|live|radio\s*edit|single\s*version|album\s*version|mono|stereo|deluxe|bonus\s*track|acoustic|demo|instrumental|anniversary|remix|edit|version)\b[^\)\]）]*[\)\]）]", RegexOptions.IgnoreCase)]
    private static partial Regex BracketNoiseRegex();

    // " - Remastered 2011" 这类尾部破折号噪声（只针对两侧带空格的破折号 → 保留 "Spider-Man"）
    [GeneratedRegex(@"\s[-–—]\s[^-–—]*\b(?:remaster(?:ed)?|re-?master|live|radio\s*edit|single\s*version|album\s*version|mono|stereo|deluxe|bonus\s*track|acoustic|demo|instrumental|anniversary|remix|edit|version)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex DashNoiseRegex();

    // 不带括号的尾部 "feat. ..."（with 因可能误伤正常标题而排除）
    [GeneratedRegex(@"\s(?:feat|ft|featuring)\b\.?\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex InlineFeatRegex();

    // " • 智能随机播放推荐" / " • 为你推荐的歌单" — 流媒体应用在艺人后面追加的
    // 上下文标注（实测：Spotify Android）。只针对两侧带空格的圆点，名字内部的符号则保留。
    [GeneratedRegex(@"\s[•·]\s.*$")]
    private static partial Regex BulletSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

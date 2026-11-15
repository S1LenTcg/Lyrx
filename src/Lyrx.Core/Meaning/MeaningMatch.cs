namespace Musebase.Core.Meaning;

/// <summary>
/// 判定搜索结果是否真的是那首歌。
///
/// Genius 和 Musixmatch 都**是无论你输入什么都会返回点什么**。实测中用非音乐的 YouTube 标题
/// （"海外热议的韩国地铁文化"）去搜索时，第一个命中结果是一首毫无关系的歌；
/// 若不核对就接收，那首歌的解说就会被当成这条音轨的"意义"挂上去。
/// <b>错误的依据比没有资料更糟</b> —— 因为会生成看起来可信、实则完全错误的文字。
///
/// 所以所有基于搜索的来源都必须通过这同一个标准。
/// </summary>
public static class MeaningMatch
{
    /// <summary>
    /// 标题必须匹配；艺术家只在已知时才要求核对。
    /// 标题只要任一方包含另一方即可认可 —— "(Remix)"、"(Live)" 之类的后缀很常见。
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
        if (names.Count == 0) return true; // 不知道艺术家时，仅凭标题就接受

        var haystack = MeaningText.Normalize(hitArtists ?? "");
        return names.Any(n => haystack.Contains(n, StringComparison.Ordinal));
    }
}

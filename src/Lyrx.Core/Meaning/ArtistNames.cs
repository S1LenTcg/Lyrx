namespace Musebase.Core.Meaning;

/// <summary>
/// 从歌手名标注中提取 **可用于比较的名字候选**。
///
/// 播放元数据里的歌手字段比想象中要乱，整串拿去比较，跟哪份文档都对不上。
/// 实测踩到两种情况。
///
/// ① **专辑名会以尾巴标签的形式粘过来** —— <c>"harry styles — harry's house"</c>。
/// ② **合作曲会用分隔符连起来** —— <c>"Lady Gaga/Bradley Cooper"</c>。可维基百科
///    的文档标题是 <c>"Shallow (Lady Gaga and Bradley Cooper song)"</c>，所以去掉标点后
///    整串做包含检查，会因为中间的 "and" 而永远无法匹配。
///    **只要确认其中一人**，就能达到滤掉同名异曲的目的。
///
/// 分隔符 **只看前后带空格的** —— 因为名字本身就含符号的情况
/// (<c>Jay-Z</c>, <c>AC/DC</c>)不能切开。即便如此仍会有像 <c>AC/DC</c> 这样被切碎的
/// 名字，由调用方按"没有可用名字就退回原始整串"的方式接纳。
/// </summary>
public static class ArtistNames
{
    /// <summary>跟在歌手名后面的专辑尾巴标签分隔符(含空格)。</summary>
    private static readonly string[] AlbumSeparators = [" — ", " – ", " • ", " · "];

    /// <summary>连接多个歌手的分隔符。只有斜杠常常不带空格，因此单独作为例外处理。</summary>
    private static readonly string[] ArtistSeparators =
        ["/", " & ", ", ", " feat. ", " feat ", " featuring ", " ft. ", " ft ", " with ", " x "];

    /// <summary>去掉 " — 专辑名" 这类尾巴标签。</summary>
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

    /// <summary>去掉专辑尾巴标签后按多个歌手切分出的名字(保持出现顺序)。</summary>
    public static IReadOnlyList<string> All(string artist)
    {
        var value = StripAlbumSuffix(artist);
        if (value.Length == 0) return [];

        var parts = value.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries
                                               | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? [value] : parts;
    }

    /// <summary>一个代表名 —— 用于构造搜索词(尾巴标签、合作歌手都是噪声)。</summary>
    public static string Primary(string artist)
    {
        var all = All(artist);
        return all.Count > 0 ? all[0] : artist.Trim();
    }
}

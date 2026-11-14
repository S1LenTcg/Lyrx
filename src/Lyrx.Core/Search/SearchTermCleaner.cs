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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddVariant(SearchTerm candidate)
        {
            var key = candidate.IsKeyword
                ? $"kw:{candidate.Keyword?.Trim()}"
                : $"t:{candidate.Title?.Trim()}|a:{candidate.Artist?.Trim()}";
            if (string.IsNullOrWhiteSpace(key) || !seen.Add(key)) return;

            // 避免与原始 term 重复
            if (term.IsKeyword && candidate.IsKeyword && string.Equals(term.Keyword, candidate.Keyword, StringComparison.OrdinalIgnoreCase))
                return;
            if (!term.IsKeyword && !candidate.IsKeyword
                && string.Equals(term.Title, candidate.Title, StringComparison.OrdinalIgnoreCase)
                && string.Equals(term.Artist, candidate.Artist, StringComparison.OrdinalIgnoreCase))
                return;

            result.Add(candidate);
        }

        if (term.IsKeyword)
        {
            var cleaned = CleanTitle(term.Keyword!);
            if (cleaned.Length > 0)
            {
                AddVariant(new SearchTerm(cleaned));
                if (TrySplitArtistTitle(cleaned, out var t, out var a))
                {
                    AddVariant(new SearchTerm(t, a));
                    AddVariant(new SearchTerm(a, t));
                }
            }
        }
        else
        {
            var title = CleanTitle(term.Title!);
            var artist = CleanArtist(term.Artist!);

            // 1. 常规清洗变体
            if (title.Length > 0)
                AddVariant(new SearchTerm(title, artist));

            // 2. 如果标题中带有书名号《歌名》，尝试提取
            var bookMatch = BookTitleRegex().Match(term.Title!);
            if (bookMatch.Success)
            {
                var bookSong = bookMatch.Groups[1].Value.Trim();
                var remaining = CleanTitle(term.Title!.Remove(bookMatch.Index, bookMatch.Length)).Trim();
                var candidateArtist = remaining.Length > 0 ? remaining : artist;
                if (bookSong.Length > 0)
                {
                    AddVariant(new SearchTerm(bookSong, candidateArtist));
                    AddVariant(new SearchTerm($"{candidateArtist} {bookSong}".Trim()));
                }
            }

            // 3. 如果标题包含分隔符（如 "周杰伦 - 晴天"）
            if (TrySplitArtistTitle(title, out var splitTitle, out var splitArtist))
            {
                AddVariant(new SearchTerm(splitTitle, splitArtist));
                AddVariant(new SearchTerm(splitArtist, splitTitle));
                AddVariant(new SearchTerm($"{splitArtist} {splitTitle}".Trim()));
                AddVariant(new SearchTerm(splitTitle, "")); // 某些平台单搜歌名更准
            }

            // 4. 如果原 Artist 为空或为站点名/UP主噪声，增加仅按标题关键词搜索的变体
            if (string.IsNullOrWhiteSpace(artist) || IsSiteName(term.Artist))
            {
                if (title.Length > 0)
                {
                    AddVariant(new SearchTerm(title, ""));
                    AddVariant(new SearchTerm(title));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 专门从 B 站、YouTube 等视频元数据中精确剥离栏目、修饰词与噪声，提取出真正的歌名与歌手。
    /// 例如："在百万豪装录音棚大声听 于文文《体面》【Hi-res】" -> 歌名: "体面", 歌手: "于文文"
    /// </summary>
    public static void ExtractVideoSongInfo(string rawTitle, string? rawArtist, out string cleanTitle, out string cleanArtist)
    {
        cleanTitle = rawTitle ?? "";
        cleanArtist = CleanArtist(rawArtist ?? "");

        if (string.IsNullOrWhiteSpace(rawTitle)) return;

        // 1. 优先提取书名号《歌名》
        var bookMatch = BookTitleRegex().Match(rawTitle);
        if (bookMatch.Success)
        {
            var song = bookMatch.Groups[1].Value.Trim();
            if (song.Length > 0)
            {
                cleanTitle = song;
                var outer = rawTitle.Remove(bookMatch.Index, bookMatch.Length);
                outer = CleanTitle(outer);
                outer = VideoNoisePrefixRegex().Replace(outer, " ");
                outer = Collapse(outer);

                if (outer.Length > 0)
                {
                    if (TrySplitArtistTitle(outer, out _, out var splitArtist) && splitArtist.Length > 0)
                        cleanArtist = splitArtist;
                    else if (!IsSiteName(outer))
                        cleanArtist = outer;
                }
                return;
            }
        }

        // 2. 清洗基础标题（去除分P、站点名、画质、方括号标签等）
        var cleaned = CleanTitle(rawTitle);
        cleaned = VideoNoisePrefixRegex().Replace(cleaned, " ");
        cleaned = Collapse(cleaned);

        // 3. 尝试分隔符切分（如 "周杰伦 - 晴天"）
        if (TrySplitArtistTitle(cleaned, out var t, out var a))
        {
            cleanTitle = t;
            cleanArtist = a;
            return;
        }

        // 4. 尝试方括号【歌手】歌名 切分（如 "【周杰伦】搁浅"）
        var bracketMatch = LeadingArtistBracketRegex().Match(rawTitle);
        if (bracketMatch.Success)
        {
            var artistCandidate = bracketMatch.Groups[1].Value.Trim();
            var remaining = rawTitle.Substring(bracketMatch.Index + bracketMatch.Length);
            remaining = CleanTitle(remaining);
            remaining = VideoNoisePrefixRegex().Replace(remaining, " ");
            remaining = Collapse(remaining);
            if (remaining.Length > 0 && !IsTagKeyword(artistCandidate))
            {
                cleanTitle = remaining;
                cleanArtist = artistCandidate;
                return;
            }
        }

        cleanTitle = cleaned.Length > 0 ? cleaned : rawTitle;
    }

    private static bool IsTagKeyword(string candidate)
    {
        var lower = candidate.ToLowerInvariant();
        return lower.Contains("mv") || lower.Contains("4k") || lower.Contains("1080p")
            || lower.Contains("翻唱") || lower.Contains("原唱") || lower.Contains("自制")
            || lower.Contains("超清") || lower.Contains("高清") || lower.Contains("现场")
            || lower.Contains("修复") || lower.Contains("双语") || lower.Contains("字幕")
            || lower.Contains("单曲") || lower.Contains("纯享") || lower.Contains("cover");
    }

    /// <summary>尝试从文本中拆分出歌名与歌手（如 "周杰伦 - 晴天"）。</summary>
    public static bool TrySplitArtistTitle(string text, out string title, out string artist)
    {
        title = "";
        artist = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var separators = new[] { " - ", " — ", " – ", " / ", " | ", " ｜ " };
        foreach (var sep in separators)
        {
            var parts = text.Split(sep, 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                // 通常左边是歌手，右边是歌名（例如 "周杰伦 - 晴天"）
                artist = parts[0];
                title = parts[1];
                return true;
            }
        }
        return false;
    }

    private static bool IsSiteName(string? text) =>
        !string.IsNullOrWhiteSpace(text) && SiteNameRegex().IsMatch(text);

    /// <summary>标题清洗：站点名 + 视频标记 + 括号/破折号噪声 + featuring 去除。</summary>
    public static string CleanTitle(string title)
    {
        var s = PartPrefixRegex().Replace(title, " "); // P1 / P01. / 【P1】去除
        s = SiteNameRegex().Replace(s, " ");
        s = VideoTagRegex().Replace(s, " ");        // 括号里是 4K/字幕/MV 这类视频标记 → 整段去掉
        s = BracketFeatRegex().Replace(s, " ");
        s = BracketNoiseRegex().Replace(s, " ");
        s = WedgeUnwrapRegex().Replace(s, " $1 "); // 【】《》 只去符号、留下内文（歌名常被包在里面）
        s = InlineVideoWordsRegex().Replace(s, " "); // 4K / 官方MV / 超清修复 等无括号标签
        s = DashNoiseRegex().Replace(s, " ");
        s = VideoQualityRegex().Replace(s, " ");    // 尾部 1080P / 60帧 / 超清 这类规格
        s = InlineFeatRegex().Replace(s, " ");
        return Collapse(s);
    }

    /// <summary>
    /// 艺人清洗：去除 featuring、站点名以及流媒体应用挂上的上下文尾标
    /// （因担心破坏多位艺人，不做分隔符切分）。
    /// </summary>
    internal static string CleanArtist(string artist)
    {
        var s = SiteNameRegex().Replace(artist, " ");
        s = BracketFeatRegex().Replace(s, " ");
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

    // " • 智能随机播放推荐"（Spotify Android）与 "艺人 — 专辑名"（Apple Music Windows 等）—
    // 只针对两侧带空白符的分隔符，艺人名内部的连字符（如 G-Dragon）则保留。
    [GeneratedRegex(@"\s+[•·—]\s+.*$")]
    private static partial Regex BulletSuffixRegex();

    // 视频站点名。B 站 UWP / AcFun 这类应用的 SMTC 元数据里，艺人常被填成站点名，
    // 标题里也常带站点后缀；留在搜索词里会让歌词提供方匹配不到。
    [GeneratedRegex(@"(?:哔哩哔哩|bilibili|b\s*站|acfun|a\s*站|youtube)", RegexOptions.IgnoreCase)]
    private static partial Regex SiteNameRegex();

    // 视频标题括号里的标记：MV / 4K / 字幕 / 双语 / 完整版 / 现场 / 翻唱 …（中英文混排）
    [GeneratedRegex(@"[\(\[【（《][^\)\]】）》]*(?:mv|pv|official|4k|8k|1080p|720p|60\s*fps|hdr|hd|hi-?res|字幕|双语|中字|中英|完整版|现场|翻唱|伴奏|纯音乐|合集|片段|剪辑|饭制|修复|超清|高清|无损|官方)[^\)\]】）》]*[\)\]】）》]", RegexOptions.IgnoreCase)]
    private static partial Regex VideoTagRegex();

    // 【歌名】/《歌名》——只去掉书名号/方头括号，保留里面的文字
    [GeneratedRegex(@"[【《]([^】》]{1,80})[】》]")]
    private static partial Regex WedgeUnwrapRegex();

    // 尾部画质/规格/试听标签：1080P / 4K / 60帧 / Hi-Res / 录音棚大声听 等（到行尾为止）
    [GeneratedRegex(@"[\s\-–—|｜]*(?:\b[48]k\b|\b\d{3,4}[pi]\b|\d{2,3}\s*帧|\d{2,3}\s*fps|\b(?:hdr|hd|hq|hi-?res)\b|超清|高清|蓝光|无损|修复版?|大声听|录音棚|试听|高音质|纯享版?|全网首发|重磅推荐)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoQualityRegex();

    // 视频分P前缀（如 "P1 "、"P02."、"【P1】"、"Part 1"）
    [GeneratedRegex(@"^(?:【?\s*(?:p(?:art)?\.?\s*\d+|\d{1,2})\s*】?[\s\.\-_:：]*)+", RegexOptions.IgnoreCase)]
    private static partial Regex PartPrefixRegex();

    // 提取书名号中的歌名（如 《晴天》）
    [GeneratedRegex(@"《([^》]{1,80})》")]
    public static partial Regex BookTitleRegex();

    // 常见的视频栏目/修饰语（允许出现在歌名前或歌名后）
    [GeneratedRegex(@"(?:(?:在?[一二三四五六七八九十百千万\d]+[豪装级]*录音棚(?:大声听|大声唱|大声放|试听)?)|沉浸式(?:听歌|体验|现场)?|耳机福利|戴上耳机|超好听|神级现场|封神现场|万人合唱|单曲循环|高清修复|百万级装备试听|录音棚试听|大声听|全网首发|重磅推荐|高能现场|必听神曲|珍藏版?|精选|热歌)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex VideoNoisePrefixRegex();

    [GeneratedRegex(@"^【([^】]{1,30})】")]
    private static partial Regex LeadingArtistBracketRegex();

    // 常见的无括号视频规格/标签
    [GeneratedRegex(@"(?:[48][kK]|\b\d{3,4}[pi]\b|\d{2,3}\s*帧|\d{2,3}\s*fps|\b(?:hdr|hd|hq|hi-?res)\b|超清修复|高清修复|超清|高清|蓝光|无损音质|无损|官方\s*[mM][vV]|[mM][vV]\b|完整版|纯享版|现场版|单曲循环)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineVideoWordsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

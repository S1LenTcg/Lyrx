namespace Musebase.Core;

/// <summary>
/// 歌词候选质量评分。LyricsKit 的 Lyrics+Quality.swift 移植。
/// 在歌手/标题/时长相似度的加权平均之上，加上翻译与字符级时间标签的奖励，
/// 并对不想要的伴奏（Instrumental/Karaoke）变体施加惩罚。
/// </summary>
public static class LyricsQuality
{
    private const double ArtistWeight = 0.45;
    private const double TitleWeight = 0.40;
    private const double DurationWeight = 0.15;

    private const double TranslationBonus = 0.10;
    private const double InlineTimeTagBonus = 0.05;

    private const double NeutralScore = 0.6;
    private const double MissingTagScore = 0.3;
    private const double MinimalDurationQuality = 0.5;
    private const double AlternateVersionPenalty = 0.3;

    private static readonly string[] AlternateVersionKeywords =
    [
        "伴奏", "无人声", "纯音乐", "卡拉ok", "伴唱",
        "instrumental", "inst.", "karaoke",
        "off vocal", "off-vocal", "offvocal",
        "acapella", "a capella",
    ];

    /// <summary>0~1（可含奖励）的质量得分。结果会缓存到 Metadata。</summary>
    public static double Quality(this Lyrics lyrics)
    {
        if (lyrics.Metadata.Quality is { } cached) return cached;

        var baseScore = ArtistQuality(lyrics) * ArtistWeight
                      + TitleQuality(lyrics) * TitleWeight
                      + DurationQuality(lyrics) * DurationWeight;
        var quality = Math.Clamp(baseScore, 0.0, 1.0);

        if (lyrics.HasTranslation()) quality += TranslationBonus;
        if (lyrics.Metadata.AttachmentTags.Contains(LineAttachments.TagTimeTag)) quality += InlineTimeTagBonus;
        if (IsUnwantedAlternateVersion(lyrics)) quality -= AlternateVersionPenalty;

        lyrics.Metadata.Quality = quality;
        return quality;
    }

    /// <summary>是否至少有一行带有翻译</summary>
    public static bool HasTranslation(this Lyrics lyrics) =>
        lyrics.Lines.Any(l => l.Attachments.Translation() is not null);

    /// <summary>标题与歌手是否与搜索词（达到包含关系级别）匹配 —— 用于判定缓存命中</summary>
    public static bool IsMatched(this Lyrics lyrics)
    {
        var artist = lyrics.IdTags.GetValueOrDefault(Lyrics.TagArtist);
        var title = lyrics.IdTags.GetValueOrDefault(Lyrics.TagTitle);
        if (artist is null || title is null) return false;

        var term = lyrics.Metadata.Request?.Term;
        if (term is null) return false;

        if (term.IsKeyword)
        {
            return IsCaseInsensitiveSimilar(title, term.Keyword!)
                && IsCaseInsensitiveSimilar(artist, term.Keyword!);
        }
        return IsCaseInsensitiveSimilar(title, term.Title!)
            && IsCaseInsensitiveSimilar(artist, term.Artist!);
    }

    /// <summary>
    /// 校验候选歌词是否与目标曲目合理匹配，避免张冠李戴（例如目标是《体面》，候选歌词却是同歌手的《听说你》）。
    /// </summary>
    public static bool IsPlausibleMatch(this Lyrics lyrics, string targetTitle, string? targetArtist = null)
    {
        var candTitle = lyrics.IdTags.GetValueOrDefault(Lyrics.TagTitle);
        if (string.IsNullOrWhiteSpace(candTitle)) return true; // 无法得知候选歌词标题时不拦截

        var trackTitle = (targetTitle ?? "").Trim();
        if (trackTitle.Length == 0) return true;

        var cleanCand = Search.SearchTermCleaner.CleanTitle(candTitle);
        var cleanTrack = Search.SearchTermCleaner.CleanTitle(trackTitle);

        // 1. 包含关系（如 "体面" 与 "体面 (电影插曲)"）
        if (cleanCand.Contains(cleanTrack, StringComparison.OrdinalIgnoreCase) ||
            cleanTrack.Contains(cleanCand, StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. 对称编辑距离相似度 >= 0.4
        var sim = Similarity(cleanCand.ToLowerInvariant(), cleanTrack.ToLowerInvariant());
        if (sim >= 0.4) return true;

        // 3. 若目标标题含《书名号》，必须与书名号内歌名相似
        var bookMatch = Search.SearchTermCleaner.BookTitleRegex().Match(trackTitle);
        if (bookMatch.Success)
        {
            var bookSong = bookMatch.Groups[1].Value.Trim();
            if (cleanCand.Contains(bookSong, StringComparison.OrdinalIgnoreCase) ||
                bookSong.Contains(cleanCand, StringComparison.OrdinalIgnoreCase) ||
                Similarity(cleanCand.ToLowerInvariant(), bookSong.ToLowerInvariant()) >= 0.4)
                return true;
        }

        return false;
    }

    private static bool IsUnwantedAlternateVersion(Lyrics lyrics)
    {
        var title = lyrics.IdTags.GetValueOrDefault(Lyrics.TagTitle);
        if (title is null) return false;
        var lowered = title.ToLowerInvariant();
        if (!AlternateVersionKeywords.Any(lowered.Contains)) return false;

        var term = lyrics.Metadata.Request?.Term;
        if (term is null) return true;
        var searchText = (term.Keyword ?? term.Title!).ToLowerInvariant();
        return !AlternateVersionKeywords.Any(searchText.Contains);
    }

    private static double ArtistQuality(Lyrics lyrics)
    {
        var term = lyrics.Metadata.Request?.Term;
        if (term is null) return NeutralScore;
        var artist = lyrics.IdTags.GetValueOrDefault(Lyrics.TagArtist);

        if (term.IsKeyword)
        {
            if (string.IsNullOrEmpty(artist)) return NeutralScore;
            return ContainmentSimilarity(artist.ToLowerInvariant(), term.Keyword!.ToLowerInvariant());
        }
        if (string.IsNullOrEmpty(term.Artist)) return NeutralScore;
        if (string.IsNullOrEmpty(artist)) return MissingTagScore;
        return Similarity(artist.ToLowerInvariant(), term.Artist.ToLowerInvariant());
    }

    private static double TitleQuality(Lyrics lyrics)
    {
        var term = lyrics.Metadata.Request?.Term;
        if (term is null) return NeutralScore;
        var title = lyrics.IdTags.GetValueOrDefault(Lyrics.TagTitle);

        if (term.IsKeyword)
        {
            if (string.IsNullOrEmpty(title)) return NeutralScore;
            return ContainmentSimilarity(title.ToLowerInvariant(), term.Keyword!.ToLowerInvariant());
        }
        if (string.IsNullOrEmpty(term.Title)) return NeutralScore;
        if (string.IsNullOrEmpty(title)) return MissingTagScore;
        return Similarity(title.ToLowerInvariant(), term.Title.ToLowerInvariant());
    }

    private static double DurationQuality(Lyrics lyrics)
    {
        var duration = lyrics.Length;
        var searchDuration = lyrics.Metadata.Request?.Duration ?? 0;
        if (duration is null || searchDuration <= 0) return NeutralScore;

        var dt = Math.Abs(searchDuration - duration.Value);
        if (dt >= 10) return MinimalDurationQuality;
        return 1 - Math.Pow(dt / 10, 2) * (1 - MinimalDurationQuality);
    }

    private static bool IsCaseInsensitiveSimilar(string a, string b)
    {
        var s1 = a.ToLowerInvariant();
        var s2 = b.ToLowerInvariant();
        return s1.Contains(s2) || s2.Contains(s1);
    }

    /// <summary>对称相似度：以较短一方的长度为基准，在“插入免费”与“删除免费”中取更有利的一侧</summary>
    internal static double Similarity(string s1, string s2)
    {
        var len = Math.Min(s1.Length, s2.Length);
        if (len == 0) return 0;
        var diff = Math.Min(
            EditDistance(s1, s2, insertionCost: 0),
            EditDistance(s1, s2, deletionCost: 0));
        return Math.Clamp((double)(len - diff) / len, 0.0, 1.0);
    }

    /// <summary>包含相似度：s1 有多少内容落在搜索关键词（s2）之中</summary>
    internal static double ContainmentSimilarity(string s1, string s2)
    {
        var len = Math.Max(s1.Length, s2.Length);
        if (len == 0) return 1;
        var diff = EditDistance(s1, s2, insertionCost: 0);
        return Math.Clamp((double)(len - diff) / len, 0.0, 1.0);
    }

    /// <summary>可变代价编辑距离（与 Swift 原版的 DP 算法完全一致）</summary>
    internal static int EditDistance(string s1, string s2, int substitutionCost = 1, int insertionCost = 1, int deletionCost = 1)
    {
        var d = new int[s2.Length + 1];
        for (var i = 0; i <= s2.Length; i++) d[i] = i;

        foreach (var c1 in s1)
        {
            var t = d[0];
            d[0]++;
            for (var i = 0; i < s2.Length; i++)
            {
                var t2 = d[i + 1];
                d[i + 1] = c1 == s2[i]
                    ? t
                    : Math.Min(Math.Min(t + substitutionCost, d[i] + insertionCost), t2 + deletionCost);
                t = t2;
            }
        }
        return d[s2.Length];
    }
}

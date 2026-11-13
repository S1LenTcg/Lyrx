namespace Musebase.Core;

/// <summary>
/// 가사 후보 품질 산정. LyricsKit의 Lyrics+Quality.swift 포팅.
/// 아티스트/제목/길이 유사도의 가중 평균에 번역·글자단위 타임태그 보너스,
/// 원치 않는 반주(Instrumental/Karaoke) 변형 페널티를 적용한다.
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

    /// <summary>0~1(+보너스) 품질 점수. 결과는 Metadata에 캐시된다.</summary>
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

    /// <summary>번역이 붙은 라인이 하나라도 있는가</summary>
    public static bool HasTranslation(this Lyrics lyrics) =>
        lyrics.Lines.Any(l => l.Attachments.Translation() is not null);

    /// <summary>제목·아티스트가 검색어와 (포함 관계 수준으로) 일치하는가 — 캐시 히트 판정용</summary>
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

    /// <summary>대칭 유사도: 짧은 쪽 길이 기준, 삽입 또는 삭제 무료 중 유리한 쪽</summary>
    internal static double Similarity(string s1, string s2)
    {
        var len = Math.Min(s1.Length, s2.Length);
        if (len == 0) return 0;
        var diff = Math.Min(
            EditDistance(s1, s2, insertionCost: 0),
            EditDistance(s1, s2, deletionCost: 0));
        return Math.Clamp((double)(len - diff) / len, 0.0, 1.0);
    }

    /// <summary>포함 유사도: s1이 검색 키워드(s2) 안에 얼마나 들어있는가</summary>
    internal static double ContainmentSimilarity(string s1, string s2)
    {
        var len = Math.Max(s1.Length, s2.Length);
        if (len == 0) return 1;
        var diff = EditDistance(s1, s2, insertionCost: 0);
        return Math.Clamp((double)(len - diff) / len, 0.0, 1.0);
    }

    /// <summary>가변 비용 편집 거리 (Swift 원본의 DP 알고리즘 그대로)</summary>
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

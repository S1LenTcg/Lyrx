namespace Musebase.Core;

/// <summary>
/// 번역 가사 병합. LyricsKit의 Lyrics+Merge.swift 포팅.
/// </summary>
public static class LyricsMergeExtensions
{
    private const double MergeTimeTagThreshold = 0.02;

    /// <summary>
    /// 타임태그가 (거의) 일치하는 라인끼리 번역을 병합한다.
    /// 두 목록 모두 위치 오름차순 정렬 전제.
    /// </summary>
    public static void MergeTranslation(this Lyrics lyrics, Lyrics translation)
    {
        int i = 0, t = 0;
        while (i < lyrics.Lines.Count && t < translation.Lines.Count)
        {
            var diff = lyrics.Lines[i].Position - translation.Lines[t].Position;
            if (Math.Abs(diff) < MergeTimeTagThreshold)
            {
                ApplyTranslation(lyrics.Lines[i], translation.Lines[t].Content);
                i++;
                t++;
            }
            else if (diff > 0)
            {
                t++;
            }
            else
            {
                i++;
            }
        }
    }

    /// <summary>라인 수가 같을 때 타임태그 무시하고 1:1 병합 (KRC 등 정확 매칭 불가 형식용)</summary>
    public static void ForceMergeTranslation(this Lyrics lyrics, Lyrics translation)
    {
        if (lyrics.Lines.Count != translation.Lines.Count) return;
        for (var i = 0; i < lyrics.Lines.Count; i++)
            ApplyTranslation(lyrics.Lines[i], translation.Lines[i].Content);
    }

    private static void ApplyTranslation(LyricsLine line, string translation)
    {
        // "//"는 NetEase가 빈 번역 라인에 쓰는 플레이스홀더
        if (translation.Length > 0 && translation != "//")
            line.Attachments[LineAttachments.TranslationTag()] = translation;
    }
}

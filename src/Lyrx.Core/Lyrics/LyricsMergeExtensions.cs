namespace Musebase.Core;

/// <summary>
/// 翻译歌词合并。LyricsKit 的 Lyrics+Merge.swift 移植。
/// </summary>
public static class LyricsMergeExtensions
{
    private const double MergeTimeTagThreshold = 0.02;

    /// <summary>
    /// 在时间标签（几乎）一致的行之间合并翻译。
    /// 前提：两个列表均按位置升序排列。
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

    /// <summary>行数相同时忽略时间标签按 1:1 合并（用于 KRC 等无法精确匹配的格式）</summary>
    public static void ForceMergeTranslation(this Lyrics lyrics, Lyrics translation)
    {
        if (lyrics.Lines.Count != translation.Lines.Count) return;
        for (var i = 0; i < lyrics.Lines.Count; i++)
            ApplyTranslation(lyrics.Lines[i], translation.Lines[i].Content);
    }

    private static void ApplyTranslation(LyricsLine line, string translation)
    {
        // "//" 是 NetEase 在空翻译行中使用的占位符
        if (translation.Length > 0 && translation != "//")
            line.Attachments[LineAttachments.TranslationTag()] = translation;
    }
}

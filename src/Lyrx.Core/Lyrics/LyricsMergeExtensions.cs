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

    /// <summary>
    /// 面向逐字时间轴（如 NetEase yrc/klyric）的翻译合并：
    /// 先执行 20ms 精确时间戳匹配；若大量译文因逐字行首时间与行级 lrc 存在小幅偏移而未命中，
    /// 再对剩余行在单调顺序与 ±0.85s 容差内按最邻近时间戳对齐补齐。
    /// </summary>
    internal static void MergeTranslationAdaptive(this Lyrics lyrics, Lyrics translation)
    {
        lyrics.MergeTranslation(translation);

        var totalNonEmpty = translation.Lines.Count(l => IsValidTranslationText(l.Content));
        if (totalNonEmpty < 3) return;

        var trTag = LineAttachments.TranslationTag();
        var exactMatched = lyrics.Lines.Count(l => !string.IsNullOrWhiteSpace(l.Attachments[trTag]));
        if (exactMatched * 2 >= totalNonEmpty) return;

        // 建立译文到原文行的近似单调映射，只补齐尚无译文的行
        var matchedTransPositions = new HashSet<int>();
        var lStart = 0;
        for (var ti = 0; ti < translation.Lines.Count; ti++)
        {
            var tLine = translation.Lines[ti];
            if (!IsValidTranslationText(tLine.Content)) continue;

            // 若该译文时间戳已在 20ms 精确阶段命中，则推进下界游标
            var exactIdx = -1;
            for (var li = lStart; li < lyrics.Lines.Count; li++)
            {
                if (Math.Abs(lyrics.Lines[li].Position - tLine.Position) < MergeTimeTagThreshold)
                {
                    exactIdx = li;
                    break;
                }
                if (lyrics.Lines[li].Position > tLine.Position + MergeTimeTagThreshold)
                    break;
            }
            if (exactIdx >= 0)
            {
                lStart = exactIdx + 1;
                matchedTransPositions.Add(ti);
                continue;
            }

            if (matchedTransPositions.Contains(ti)) continue;

            var bestIdx = -1;
            var bestDiff = 0.85;
            for (var li = lStart; li < lyrics.Lines.Count; li++)
            {
                var line = lyrics.Lines[li];
                if (!line.Enabled || string.IsNullOrWhiteSpace(line.Content)) continue;
                if (!string.IsNullOrWhiteSpace(line.Attachments[trTag])) continue;

                var diff = Math.Abs(line.Position - tLine.Position);
                if (line.Position > tLine.Position + 0.85) break;
                if (diff <= bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = li;
                }
            }

            if (bestIdx >= 0)
            {
                ApplyTranslation(lyrics.Lines[bestIdx], tLine.Content);
                lStart = bestIdx + 1;
            }
        }
    }

    private static bool IsValidTranslationText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim();
        return trimmed.Length > 0 && trimmed != "//";
    }

    private static void ApplyTranslation(LyricsLine line, string translation)
    {
        // "//" 是 NetEase 在空翻译行中使用的占位符
        if (translation.Length > 0 && translation != "//")
            line.Attachments[LineAttachments.TranslationTag()] = translation;
    }
}

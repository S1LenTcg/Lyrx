using System.Text;

namespace Musebase.Core;

/// <summary>
/// 歌词编辑窗口的“简洁视图”支持。只把指定翻译语言序列化为 [时间]原文【译文】，
/// 并把编辑结果合并回原始模型（保留位置相同行的字符级标签与其他语言的翻译）。
/// </summary>
public static class LyricsEditing
{
    /// <summary>歌词中存在的翻译标签列表（始终包含 generic "tr"）。例如：["tr", "tr:ja", "tr:ko"]。</summary>
    public static IReadOnlyList<string> TranslationTags(Lyrics lyrics)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal) { LineAttachments.TagTranslationPrefix };
        foreach (var line in lyrics.Lines)
            foreach (var key in line.Attachments.Content.Keys)
                if (IsTranslationTag(key))
                    set.Add(key);
        return set.ToList();
    }

    private static bool IsTranslationTag(string key) =>
        key == LineAttachments.TagTranslationPrefix
        || key.StartsWith(LineAttachments.TagTranslationPrefix + ":", StringComparison.Ordinal);

    /// <summary>只包含所选语言（tag）内联翻译的简洁文本：[时间]原文【译文】。</summary>
    public static string ToSimpleText(Lyrics lyrics, string tag)
    {
        var sb = new StringBuilder();
        foreach (var line in lyrics.Lines)
        {
            sb.Append('[').Append(line.TimeTag).Append(']').Append(line.Content);
            var tr = line.Attachments[tag];
            if (!string.IsNullOrEmpty(tr)) sb.Append('【').Append(tr).Append('】');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 把简洁文本的编辑结果合并回原始数据。保留原始数据中的 ID 标签，
    /// 对位置相同的行保留字符级标签（tt）以及非编辑对象的语言翻译。
    /// 解析失败时返回 null。
    /// </summary>
    public static Lyrics? ApplySimpleEdit(Lyrics original, string simpleText, string tag)
    {
        var parsed = Lyrics.Parse(simpleText);
        if (parsed is null || parsed.Lines.Count == 0) return null;

        var byPos = new Dictionary<long, LyricsLine>();
        foreach (var line in original.Lines)
            byPos[PosKey(line.Position)] = line;

        var result = new Lyrics(Array.Empty<LyricsLine>(), original.IdTags);
        foreach (var parsedLine in parsed.Lines)
        {
            var att = new LineAttachments();
            // 从位置相同的原始行恢复 tt 与其他语言的翻译（排除编辑对象的标签）
            if (byPos.TryGetValue(PosKey(parsedLine.Position), out var orig))
                foreach (var (key, value) in orig.Attachments.Content)
                    if (key != tag)
                        att[key] = value;

            // 把编辑后的翻译（简洁文本中的【】会被解析为 generic "tr"）设置为所选标签
            var editedTranslation = parsedLine.Attachments.Translation();
            if (!string.IsNullOrEmpty(editedTranslation))
                att[tag] = editedTranslation;

            result.Lines.Add(new LyricsLine(parsedLine.Content, parsedLine.Position, att));
        }

        var attachmentTags = new HashSet<string>();
        foreach (var line in result.Lines)
            foreach (var key in line.Attachments.Content.Keys)
                attachmentTags.Add(key);
        result.Metadata.AttachmentTags = attachmentTags;

        return result;
    }

    private static long PosKey(double positionSeconds) => (long)Math.Round(positionSeconds * 1000);
}

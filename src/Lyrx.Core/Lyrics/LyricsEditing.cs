using System.Text;

namespace Musebase.Core;

/// <summary>
/// 가사 편집 창의 "간편 보기" 지원. 특정 번역 언어만 [시간]원문【번역】으로 직렬화하고,
/// 편집 결과를 원본 모델에 병합한다(위치가 일치하는 줄의 글자단위 태그·타 언어 번역은 보존).
/// </summary>
public static class LyricsEditing
{
    /// <summary>가사에 존재하는 번역 태그 목록(항상 generic "tr" 포함). 예: ["tr", "tr:ja", "tr:ko"].</summary>
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

    /// <summary>선택 언어(tag)만 인라인 번역으로 담은 간편 텍스트: [시간]원문【번역】.</summary>
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
    /// 간편 텍스트 편집 결과를 원본에 병합한다. 원본의 ID 태그를 유지하고,
    /// 위치가 일치하는 줄에서는 글자단위 태그(tt)와 편집 대상이 아닌 언어의 번역을 보존한다.
    /// 파싱 실패 시 null.
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
            // 위치가 같은 원본 줄에서 tt·타 언어 번역 복원(편집 대상 태그는 제외)
            if (byPos.TryGetValue(PosKey(parsedLine.Position), out var orig))
                foreach (var (key, value) in orig.Attachments.Content)
                    if (key != tag)
                        att[key] = value;

            // 편집된 번역(간편 텍스트의 【】는 generic "tr"로 파싱됨)을 선택 태그로 설정
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

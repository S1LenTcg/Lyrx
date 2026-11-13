using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Musebase.Core.Search;

/// <summary>
/// QQ 音乐 QRC 본문 파서. LyricsKit의 QQMusicQrcParser.swift 포팅.
/// [startMs,durationMs]본문 라인과 글자단위 fragment(absStartMs,durationMs) 인라인 태그를 해석한다.
/// </summary>
internal static class QrcParser
{
    public static Lyrics? Parse(string content)
    {
        content = content.Replace("\r\n", "\n").Replace('\r', '\n');

        var idTags = new Dictionary<string, string>();
        foreach (Match m in LrcRegex.Id3Tag().Matches(content))
        {
            var key = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (key.Length > 0 && value.Length > 0) idTags[key] = value;
        }

        var lyrics = new Lyrics();
        foreach (Match m in LrcRegex.KrcLine().Matches(content))
        {
            var startMs = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var start = startMs / 1000.0;
            var duration = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 1000.0;

            var sb = new StringBuilder();
            var entries = new List<InlineTimeTags.Entry> { new(0, 0) };
            foreach (Match im in LrcRegex.QqInlineTag().Matches(m.Groups[3].Value))
            {
                var fragment = im.Groups[1].Value;
                var t1 = int.Parse(im.Groups[2].Value, CultureInfo.InvariantCulture) - startMs;
                var t2 = int.Parse(im.Groups[3].Value, CultureInfo.InvariantCulture);
                var t = (t1 + t2) / 1000.0;
                var prev = sb.Length;
                sb.Append(fragment);
                if (sb.Length > prev) entries.Add(new(sb.Length, t));
            }

            var att = new LineAttachments
            {
                [LineAttachments.TagTimeTag] = new InlineTimeTags(entries, duration).ToString(),
            };
            lyrics.Lines.Add(new LyricsLine(sb.ToString(), start, att));
        }
        if (lyrics.Lines.Count == 0) return null;

        foreach (var (k, v) in idTags) lyrics.IdTags[k] = v;
        lyrics.Metadata.AttachmentTags = new HashSet<string> { LineAttachments.TagTimeTag };
        return lyrics;
    }
}

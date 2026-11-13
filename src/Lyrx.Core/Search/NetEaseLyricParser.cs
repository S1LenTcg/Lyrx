using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Musebase.Core.Search;

/// <summary>
/// NetEase 逐字歌词解析器。LyricsKit 的 NetEaseKLyricParser.swift 移植。
/// yrc（新版逐词）与 klyric（旧版卡拉OK）都解析 [startMs,durationMs]正文行以及
/// 逐字内联标签，产出 InlineTimeTags(`tt`)。
/// </summary>
internal static class NetEaseLyricParser
{
    /// <summary>yrc: 内联标签 (absStartMs,durMs,0)fragment</summary>
    public static Lyrics? ParseYrc(string content)
    {
        content = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lyrics = new Lyrics();

        foreach (Match m in LrcRegex.KrcLine().Matches(content))
        {
            var startMs = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var start = startMs / 1000.0;
            var duration = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 1000.0;

            var sb = new StringBuilder();
            var entries = new List<InlineTimeTags.Entry> { new(0, 0) };
            foreach (Match im in LrcRegex.NetEaseYrcInlineTag().Matches(m.Groups[3].Value))
            {
                var t1 = int.Parse(im.Groups[1].Value, CultureInfo.InvariantCulture) - startMs;
                var t2 = int.Parse(im.Groups[2].Value, CultureInfo.InvariantCulture);
                var t = (t1 + t2) / 1000.0;
                var prev = sb.Length;
                sb.Append(im.Groups[3].Value);
                if (sb.Length > prev) entries.Add(new(sb.Length, t));
            }

            lyrics.Lines.Add(NewLine(sb.ToString(), start, entries, duration));
        }

        return Finalize(lyrics, content);
    }

    /// <summary>klyric: 内联标签 (0,durMs)fragment。时间按持续时间累加（相对行起始的偏移）。</summary>
    public static Lyrics? ParseKLyric(string content)
    {
        content = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var lyrics = new Lyrics();

        foreach (Match m in LrcRegex.KrcLine().Matches(content))
        {
            var start = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;
            var duration = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 1000.0;

            var sb = new StringBuilder();
            var entries = new List<InlineTimeTags.Entry> { new(0, 0) };
            var dt = 0.0;
            foreach (Match im in LrcRegex.NetEaseKLyricInlineTag().Matches(m.Groups[3].Value))
            {
                var span = int.Parse(im.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0;
                var fragment = im.Groups[2].Value;
                if (im.Groups[3].Success && im.Groups[3].Value.Length > 0)
                {
                    span += 0.001;
                    fragment += " ";
                }
                sb.Append(fragment);
                dt += span;
                entries.Add(new(sb.Length, dt));
            }

            lyrics.Lines.Add(NewLine(sb.ToString(), start, entries, duration));
        }

        return Finalize(lyrics, content);
    }

    private static LyricsLine NewLine(string content, double position, List<InlineTimeTags.Entry> entries, double duration)
    {
        var att = new LineAttachments
        {
            [LineAttachments.TagTimeTag] = new InlineTimeTags(entries, duration).ToString(),
        };
        return new LyricsLine(content, position, att);
    }

    private static Lyrics? Finalize(Lyrics lyrics, string content)
    {
        if (lyrics.Lines.Count == 0) return null;

        foreach (Match m in LrcRegex.Id3Tag().Matches(content))
        {
            var key = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (key.Length > 0 && value.Length > 0) lyrics.IdTags[key] = value;
        }

        lyrics.Metadata.AttachmentTags = new HashSet<string> { LineAttachments.TagTimeTag };
        return lyrics;
    }
}

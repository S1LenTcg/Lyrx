using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Musebase.Core.Search;

/// <summary>
/// Kugou KRC 본문 파서. LyricsKit의 KugouKrcParser.swift 포팅.
/// [startMs,durationMs]본문 라인과 글자단위 &lt;t1,t2,0&gt; 인라인 태그를 해석하고,
/// [language:base64] 헤더의 번역(type==1)을 라인에 병합한다.
/// </summary>
internal static class KrcParser
{
    public static Lyrics? Parse(string content)
    {
        content = content.Replace("\r\n", "\n").Replace('\r', '\n');

        var idTags = new Dictionary<string, string>();
        KugouLanguageHeader? languageHeader = null;

        foreach (Match m in LrcRegex.Id3Tag().Matches(content))
        {
            var key = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (key.Length == 0 || value.Length == 0) continue;

            if (key == "language")
                languageHeader = TryDecodeLanguageHeader(value);
            else
                idTags[key] = value;
        }

        var lyrics = new Lyrics();
        foreach (Match m in LrcRegex.KrcLine().Matches(content))
        {
            var start = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 1000;
            var duration = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / 1000;

            var sb = new StringBuilder();
            var entries = new List<InlineTimeTags.Entry> { new(0, 0) };
            foreach (Match im in LrcRegex.KugouInlineTag().Matches(m.Groups[3].Value))
            {
                var t1 = int.Parse(im.Groups[1].Value, CultureInfo.InvariantCulture);
                var t2 = int.Parse(im.Groups[2].Value, CultureInfo.InvariantCulture);
                var t = (t1 + t2) / 1000.0;
                var fragment = im.Groups[3].Value;
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

        // type == 1: 번역, type == 0: 발음(로마자). 번역만 첨부한다.
        var trans = languageHeader?.Content?.FirstOrDefault(c => c.Type == 1);
        if (trans?.LyricContent is { } lyricContent)
        {
            var count = Math.Min(lyricContent.Count, lyrics.Lines.Count);
            var applied = false;
            for (var i = 0; i < count; i++)
            {
                var str = string.Concat(lyricContent[i]).Trim();
                if (str.Length == 0) continue;
                lyrics.Lines[i].Attachments[LineAttachments.TranslationTag()] = str;
                applied = true;
            }
            if (applied)
                lyrics.Metadata.AttachmentTags =
                    new HashSet<string>(lyrics.Metadata.AttachmentTags) { LineAttachments.TranslationTag() };
        }

        return lyrics;
    }

    private static KugouLanguageHeader? TryDecodeLanguageHeader(string base64)
    {
        try
        {
            var json = Convert.FromBase64String(base64);
            return JsonSerializer.Deserialize<KugouLanguageHeader>(json);
        }
        catch
        {
            return null;
        }
    }

    internal sealed record KugouLanguageHeader(
        [property: JsonPropertyName("content")] List<KugouLanguageContent>? Content,
        [property: JsonPropertyName("version")] int Version);

    internal sealed record KugouLanguageContent(
        [property: JsonPropertyName("language")] int Language,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("lyricContent")] List<List<string>>? LyricContent);
}

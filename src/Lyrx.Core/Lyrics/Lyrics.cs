using System.Globalization;
using System.Text.RegularExpressions;

namespace Musebase.Core;

/// <summary>
/// 同步歌词文档。LyricsKit 的 Lyrics 类移植。
/// LRC（+扩展：多重时间标签、【】翻译、[tt]/[tr] 附加行）的解析/序列化，
/// 以及播放位置 → 当前/下一行的查询。
/// </summary>
public sealed class Lyrics
{
    public List<LyricsLine> Lines { get; } = new();
    public Dictionary<string, string> IdTags { get; } = new();
    public LyricsMetadata Metadata { get; } = new();

    // 标准 ID 标签键
    public const string TagTitle = "ti";
    public const string TagAlbum = "al";
    public const string TagArtist = "ar";
    public const string TagAuthor = "au";
    public const string TagLrcBy = "by";
    public const string TagOffset = "offset";
    public const string TagLength = "length";

    public Lyrics() { }

    public Lyrics(IEnumerable<LyricsLine> lines, IDictionary<string, string>? idTags = null)
    {
        Lines.AddRange(lines);
        if (idTags is not null)
        {
            foreach (var (k, v) in idTags) IdTags[k] = v;
        }
    }

    /// <summary>解析 LRC 文本。没有有效歌词行时返回 null。</summary>
    public static Lyrics? Parse(string text)
    {
        // .NET 正则的 ^/$ 多行锚点以 \n 为基准，因此先把 CRLF/CR 归一化
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var lyrics = new Lyrics();

        foreach (Match m in LrcRegex.Id3Tag().Matches(text))
        {
            var key = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (value.Length > 0) lyrics.IdTags[key] = value;
        }

        foreach (Match m in LrcRegex.LyricsLine().Matches(text))
        {
            var timeTags = LrcRegex.ResolveTimeTags(m.Groups[1].Value);
            var content = m.Groups[2].Value;

            var attachments = new LineAttachments();
            if (m.Groups[3].Success && m.Groups[3].Value.Length > 0)
                attachments[LineAttachments.TranslationTag()] = m.Groups[3].Value;

            foreach (var t in timeTags)
                lyrics.Lines.Add(new LyricsLine(content, t, new LineAttachments(attachments)));
        }

        if (lyrics.Lines.Count == 0) return null;
        var sorted = lyrics.Lines.OrderBy(l => l.Position).ToList();
        lyrics.Lines.Clear();
        lyrics.Lines.AddRange(sorted);

        var attachmentTags = new HashSet<string>();
        foreach (Match m in LrcRegex.LineAttachment().Matches(text))
        {
            var timeTags = LrcRegex.ResolveTimeTags(m.Groups[1].Value);
            var tag = m.Groups[2].Value;
            var value = m.Groups[3].Value;

            foreach (var t in timeTags)
            {
                var (found, index) = lyrics.FindLineIndex(t);
                if (found) lyrics.Lines[index].Attachments[tag] = value;
            }
            attachmentTags.Add(tag);
        }
        lyrics.Metadata.AttachmentTags = attachmentTags;

        lyrics.FoldInterleavedTranslations();

        return lyrics;
    }

    /// <summary>
    /// 折叠“同时间戳双语交错”LRC（例如部分歌词源把原文和译文写成同时间戳的连续两行）：
    /// 若整首歌存在多组同时间戳配对，则把第二行并入第一行的默认译文标签，受「文A」开关统一控制。
    /// </summary>
    private void FoldInterleavedTranslations()
    {
        const double sameTimeThreshold = 0.02;

        static bool IsCreditLine(string s) =>
            s.Contains('：') || s.Contains(':') || s.StartsWith("作词", StringComparison.Ordinal) || s.StartsWith("作曲", StringComparison.Ordinal);

        bool CanPair(int i)
        {
            if (i + 1 >= Lines.Count) return false;
            var a = Lines[i];
            var b = Lines[i + 1];
            if (Math.Abs(b.Position - a.Position) >= sameTimeThreshold) return false;
            if (string.IsNullOrWhiteSpace(a.Content) || string.IsNullOrWhiteSpace(b.Content)) return false;
            if (a.Attachments.Translation() is not null || b.Attachments.Translation() is not null) return false;
            if (string.Equals(a.Content.Trim(), b.Content.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
            if (a.Position <= 0.5 && IsCreditLine(a.Content) && IsCreditLine(b.Content)) return false;
            return true;
        }

        var pairCount = 0;
        for (var i = 0; i < Lines.Count - 1; i++)
        {
            if (CanPair(i))
            {
                pairCount++;
                i++;
            }
        }
        if (pairCount < 3) return;

        var folded = new List<LyricsLine>(Lines.Count - pairCount);
        for (var i = 0; i < Lines.Count; i++)
        {
            var current = Lines[i];
            if (CanPair(i))
            {
                var tr = Lines[i + 1].Content.Trim();
                if (tr.Length > 0 && tr != "//")
                    current.Attachments[LineAttachments.TranslationTag()] = tr;
                i++;
            }
            folded.Add(current);
        }
        Lines.Clear();
        Lines.AddRange(folded);
    }

    /// <summary>[offset:] 标签（毫秒）</summary>
    public int Offset
    {
        get => IdTags.TryGetValue(TagOffset, out var v) && int.TryParse(v, out var i) ? i : 0;
        set => IdTags[TagOffset] = value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Offset 的秒单位表示</summary>
    public double TimeDelay
    {
        get => Offset / 1000.0;
        set => Offset = (int)(value * 1000);
    }

    /// <summary>[length:] 标签（秒）。"mm:ss" 或 "ss.x" 格式。</summary>
    public double? Length
    {
        get
        {
            if (!IdTags.TryGetValue(TagLength, out var len)) return null;
            var m = LrcRegex.Base60Time().Match(len);
            if (!m.Success) return null;
            var min = m.Groups[1].Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            var sec = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return min * 60 + sec;
        }
    }

    /// <summary>
    /// 播放位置对应的（当前行，下一行）索引。
    /// 未启用（enabled=false）的行会被跳过。LyricsKit 的 subscript 移植。
    /// </summary>
    public (int? CurrentIndex, int? NextIndex) LineIndexesAt(double position)
    {
        var (found, i) = FindLineIndex(position);
        var index = found ? i + 1 : i;

        int? current = null;
        for (var j = index - 1; j >= 0; j--)
        {
            if (Lines[j].Enabled) { current = j; break; }
        }
        int? next = null;
        for (var j = index; j < Lines.Count; j++)
        {
            if (Lines[j].Enabled) { next = j; break; }
        }
        return (current, next);
    }

    /// <summary>二分查找：完全一致则返回 (true, 最后一个匹配下标)，否则返回 (false, 插入位置)</summary>
    internal (bool Found, int Index) FindLineIndex(double position)
    {
        int left = 0, right = Lines.Count - 1;
        var lastMatch = -1;
        while (left <= right)
        {
            var mid = (left + right) / 2;
            if (Lines[mid].Position < position)
            {
                left = mid + 1;
            }
            else if (position < Lines[mid].Position)
            {
                right = mid - 1;
            }
            else
            {
                lastMatch = mid;
                left = mid + 1;
            }
        }
        return lastMatch >= 0 ? (true, lastMatch) : (false, left);
    }

    /// <summary>把不符合过滤条件的行设为未启用（用于去除广告/制作名单）。</summary>
    public void Filtrate(Func<LyricsLine, bool> isIncluded)
    {
        foreach (var line in Lines)
        {
            if (!isIncluded(line)) line.Enabled = false;
        }
    }

    /// <summary>LyricsX 扩展 LRC 序列化（包含附加行）</summary>
    public override string ToString()
    {
        var components = IdTags.Select(kv => $"[{kv.Key}:{kv.Value}]")
            .Concat(Lines.Select(l => l.ToString()));
        return string.Join('\n', components);
    }

    /// <summary>
    /// 标准 LRC 序列化（翻译以 【】 内联）。
    /// 指定 preferredLang 时优先该语言的翻译（tr:{lang}），没有则回退到 generic（tr）——
    /// 按与界面显示（优先机器翻译 tr:{target}）相同的优先级导出。
    /// </summary>
    public string ToLegacyString(string? preferredLang = null)
    {
        var components = IdTags.Select(kv => $"[{kv.Key}:{kv.Value}]")
            .Concat(Lines.Select(l =>
            {
                var tr = string.IsNullOrEmpty(preferredLang)
                    ? l.Attachments.Translation()
                    : l.Attachments.Translation(preferredLang, null);
                return $"[{l.TimeTag}]{l.Content}" + (tr is not null ? $"【{tr}】" : "");
            }));
        return string.Join('\n', components);
    }
}

/// <summary>
/// 解析之外的附加信息（来源、搜索质量等）。在 M1-U4 排名中扩展。
/// </summary>
public sealed class LyricsMetadata
{
    public IReadOnlySet<string> AttachmentTags { get; internal set; } = new HashSet<string>();

    /// <summary>提供方标识符（例如 "LRCLIB"、"NetEase"）</summary>
    public string? ServiceName { get; set; }

    /// <summary>提供方内部的曲目标识令牌（用于重新获取/缓存键）</summary>
    public string? ServiceToken { get; set; }

    /// <summary>专辑封面 URL</summary>
    public Uri? ArtworkUrl { get; set; }

    /// <summary>搜索当时的请求 —— 用于排名计算</summary>
    public Search.LyricsSearchRequest? Request { get; set; }

    /// <summary>质量分缓存（由 LyricsQuality.Quality 填充）</summary>
    public double? Quality { get; internal set; }
}

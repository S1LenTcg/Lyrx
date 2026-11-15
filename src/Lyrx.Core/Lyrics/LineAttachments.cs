using System.Text.RegularExpressions;

namespace Musebase.Core;

/// <summary>
/// 歌词行附件（翻译、内联时间标签等）。LyricsKit 的 LyricsLine.Attachments 移植。
/// 值以序列化字符串保存，只由辅助方法解析出需要的类型。
/// （注音/罗马字 RangeAttribute 不属于 PRD 目标，故排除）
/// </summary>
public sealed class LineAttachments : IEquatable<LineAttachments>
{
    public const string TagTimeTag = "tt";
    public const string TagTranslationPrefix = "tr";

    private readonly Dictionary<string, string> _content;

    public LineAttachments() => _content = new Dictionary<string, string>();

    public LineAttachments(LineAttachments other) => _content = new Dictionary<string, string>(other._content);

    public IReadOnlyDictionary<string, string> Content => _content;

    public string? this[string tag]
    {
        get => _content.GetValueOrDefault(tag);
        set
        {
            if (value is null) _content.Remove(tag);
            else _content[tag] = value;
        }
    }

    public static string TranslationTag(string? languageCode = null) =>
        string.IsNullOrEmpty(languageCode) ? TagTranslationPrefix : $"{TagTranslationPrefix}:{languageCode}";

    /// <summary>
    /// 获取翻译。按语言候选顺序查找，没有候选时按 "tr" → 任意 "tr*" 标签的顺序。
    /// </summary>
    public string? Translation(params string?[] languageCodeCandidates)
    {
        var tags = new List<string>();
        foreach (var code in languageCodeCandidates)
            tags.Add(TranslationTag(code));
        if (tags.Count == 0)
        {
            tags.Add(TagTranslationPrefix);
            var any = _content.Keys.FirstOrDefault(k => k.StartsWith(TagTranslationPrefix, StringComparison.Ordinal));
            if (any is not null) tags.Add(any);
        }
        foreach (var tag in tags)
        {
            if (_content.TryGetValue(tag, out var v)) return v;
        }
        return null;
    }

    /// <summary>字符级卡拉OK用的内联时间标签。没有该标签或格式不符时返回 null。</summary>
    public InlineTimeTags? GetInlineTimeTags() =>
        _content.TryGetValue(TagTimeTag, out var raw) ? InlineTimeTags.Parse(raw) : null;

    public bool Equals(LineAttachments? other)
    {
        if (other is null || _content.Count != other._content.Count) return false;
        foreach (var (k, v) in _content)
        {
            if (!other._content.TryGetValue(k, out var ov) || ov != v) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as LineAttachments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var (k, v) in _content.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            hash.Add(k);
            hash.Add(v);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// 内联时间标签列表：&lt;msec,charIndex&gt;... + 可选的 &lt;durationMsec&gt;。
/// 以行首为基准的偏移（秒）与生效的字符索引。
/// </summary>
public sealed record InlineTimeTags(IReadOnlyList<InlineTimeTags.Entry> Tags, double? Duration)
{
    public readonly record struct Entry(int Index, double Time);

    public static InlineTimeTags? Parse(string raw)
    {
        var tags = new List<Entry>();
        foreach (Match m in LrcRegex.InlineTimeTag().Matches(raw))
        {
            var parts = m.Groups[1].Value.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], out var msec) && int.TryParse(parts[1], out var index))
                tags.Add(new Entry(index, msec / 1000.0));
        }
        if (tags.Count == 0) return null;

        double? duration = null;
        // 只把不是 <msec,idx> 形式的单独 <msec> 视为持续时长
        foreach (Match m in LrcRegex.InlineDuration().Matches(raw))
        {
            if (!m.Groups[1].Value.Contains(','))
            {
                duration = int.Parse(m.Groups[1].Value) / 1000.0;
                break;
            }
        }
        return new InlineTimeTags(tags, duration);
    }

    /// <summary>
    /// 相对于行首的经过时间（秒）所到达的（小数）字符索引。
    /// 在标签区间内做线性插值。最后一个标签之后固定在最后一个索引。
    /// 用于字符级卡拉OK填充时求出应填充到的字符位置。
    /// </summary>
    public double CharIndexAt(double time)
    {
        if (Tags.Count == 0) return 0;
        if (time <= Tags[0].Time) return Tags[0].Index;

        for (var i = 0; i < Tags.Count - 1; i++)
        {
            var a = Tags[i];
            var b = Tags[i + 1];
            if (time < b.Time)
            {
                var dt = b.Time - a.Time;
                var f = dt > 0 ? (time - a.Time) / dt : 1.0;
                return a.Index + (b.Index - a.Index) * Math.Clamp(f, 0.0, 1.0);
            }
        }
        return Tags[^1].Index;
    }

    public override string ToString()
    {
        var result = string.Concat(Tags.Select(t => $"<{(int)(t.Time * 1000)},{t.Index}>"));
        if (Duration is { } d) result += $"<{(int)(d * 1000)}>";
        return result;
    }
}

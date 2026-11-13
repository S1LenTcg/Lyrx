using System.Text.RegularExpressions;

namespace Musebase.Core;

/// <summary>
/// 가사 라인 첨부(번역, 인라인 타임태그 등). LyricsKit의 LyricsLine.Attachments 포팅.
/// 값은 직렬화 문자열로 보관하고, 필요한 타입만 헬퍼로 해석한다.
/// (후리가나/로마자 RangeAttribute는 PRD 비목표라 제외)
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
    /// 번역 취득. 언어 후보 순서대로 찾고, 후보가 없으면 "tr" → 임의의 "tr*" 태그 순.
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

    /// <summary>글자 단위 카라오케용 인라인 타임태그. 없거나 형식이 다르면 null.</summary>
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
/// 인라인 타임태그 목록: &lt;msec,charIndex&gt;... + 선택적 &lt;durationMsec&gt;.
/// 라인 시작 기준 오프셋(초)과 적용 문자 인덱스.
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
        // <msec,idx>가 아닌 단독 <msec>만 지속시간으로 취급
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
    /// 라인 시작 기준 경과 시각(초)에 도달한 (소수) 글자 인덱스.
    /// 태그 구간을 선형 보간한다. 마지막 태그 이후는 마지막 인덱스에 고정.
    /// 글자 단위 카라오케 채움에서 채울 글자 위치를 구하는 데 쓴다.
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

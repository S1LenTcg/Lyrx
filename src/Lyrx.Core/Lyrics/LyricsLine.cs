namespace Musebase.Core;

/// <summary>
/// 타임태그 1개에 대응하는 가사 한 줄. LyricsKit의 LyricsLine 포팅.
/// </summary>
public sealed class LyricsLine
{
    public string Content { get; set; }

    /// <summary>곡 시작 기준 위치(초)</summary>
    public double Position { get; set; }

    public LineAttachments Attachments { get; }

    /// <summary>필터로 제외된 라인은 false (광고 문구 등)</summary>
    public bool Enabled { get; set; } = true;

    public LyricsLine(string content, double position, LineAttachments? attachments = null)
    {
        Content = content;
        Position = position;
        Attachments = attachments ?? new LineAttachments();
    }

    /// <summary>[mm:ss.fff] 형식 타임태그 문자열</summary>
    public string TimeTag
    {
        get
        {
            var min = (int)(Position / 60);
            var sec = Position - min * 60;
            return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{min:00}:{sec:00.000}");
        }
    }

    public override string ToString()
    {
        var parts = new List<string> { Content };
        parts.AddRange(Attachments.Content.Select(kv => $"[{kv.Key}]{kv.Value}"));
        return string.Join('\n', parts.Select(p => $"[{TimeTag}]{p}"));
    }
}

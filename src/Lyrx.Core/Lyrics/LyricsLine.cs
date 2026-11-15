namespace Musebase.Core;

/// <summary>
/// 对应一个时间标签的一行歌词。LyricsKit 的 LyricsLine 移植。
/// </summary>
public sealed class LyricsLine
{
    public string Content { get; set; }

    /// <summary>相对歌曲起点的位置（秒）</summary>
    public double Position { get; set; }

    public LineAttachments Attachments { get; }

    /// <summary>被过滤排除的行为 false（广告文案等）</summary>
    public bool Enabled { get; set; } = true;

    public LyricsLine(string content, double position, LineAttachments? attachments = null)
    {
        Content = content;
        Position = position;
        Attachments = attachments ?? new LineAttachments();
    }

    /// <summary>[mm:ss.fff] 格式的时间标签字符串</summary>
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

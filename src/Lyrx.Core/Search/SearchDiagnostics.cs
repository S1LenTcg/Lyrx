namespace Musebase.Core.Search;

/// <summary>
/// 一次搜索的各来源结果摘要（用于遥测、诊断）。不包含歌曲信息。
/// 同一提供者会执行多个搜索词（原始 + 净化变体），所以按提供者（ServiceName）汇总：
/// 命中时取首个结果前的延迟（最快的请求），全败时取最后一个请求完成前的延迟。
/// </summary>
public sealed class SearchDiagnostics
{
    /// <summary>单个来源的结果：是否命中与延迟（ms）。</summary>
    public sealed record SourceStat(bool Hit, int LatencyMs);

    private readonly object _lock = new();
    private readonly Dictionary<string, SourceStat> _perSource = new();

    /// <summary>各提供者（ServiceName）的命中情况与延迟快照。</summary>
    public IReadOnlyDictionary<string, SourceStat> PerSource
    {
        get { lock (_lock) return new Dictionary<string, SourceStat>(_perSource); }
    }

    /// <summary>提供者产出了第一个结果。多次请求中只保留最快的命中。</summary>
    internal void ReportHit(string serviceName, long elapsedMs)
    {
        var ms = ClampMs(elapsedMs);
        lock (_lock)
        {
            _perSource[serviceName] = _perSource.TryGetValue(serviceName, out var s) && s.Hit
                ? s with { LatencyMs = Math.Min(s.LatencyMs, ms) }
                : new SourceStat(true, ms);
        }
    }

    /// <summary>提供者的一次请求在无结果的情况下结束。只在没有命中时保留最晚的完成延迟。</summary>
    internal void ReportMiss(string serviceName, long elapsedMs)
    {
        var ms = ClampMs(elapsedMs);
        lock (_lock)
        {
            if (_perSource.TryGetValue(serviceName, out var s))
            {
                if (!s.Hit) _perSource[serviceName] = s with { LatencyMs = Math.Max(s.LatencyMs, ms) };
            }
            else
            {
                _perSource[serviceName] = new SourceStat(false, ms);
            }
        }
    }

    private static int ClampMs(long ms) => (int)Math.Clamp(ms, 0, int.MaxValue);
}

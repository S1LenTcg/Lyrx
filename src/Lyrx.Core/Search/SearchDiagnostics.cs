namespace Musebase.Core.Search;

/// <summary>
/// 검색 1회의 소스별 결과 요약(텔레메트리·진단용). 곡 정보는 담지 않는다.
/// 같은 제공자가 여러 검색어(원본 + 정제 변형)를 수행하므로 제공자(ServiceName) 단위로 집계한다:
/// 히트 시 첫 결과까지의 지연(가장 빠른 요청), 전패 시 마지막 요청 완료까지의 지연.
/// </summary>
public sealed class SearchDiagnostics
{
    /// <summary>소스 1개의 결과: 히트 여부와 지연(ms).</summary>
    public sealed record SourceStat(bool Hit, int LatencyMs);

    private readonly object _lock = new();
    private readonly Dictionary<string, SourceStat> _perSource = new();

    /// <summary>제공자(ServiceName)별 히트 여부·지연 스냅샷.</summary>
    public IReadOnlyDictionary<string, SourceStat> PerSource
    {
        get { lock (_lock) return new Dictionary<string, SourceStat>(_perSource); }
    }

    /// <summary>제공자가 첫 결과를 냈다. 여러 요청 중 가장 빠른 히트만 남긴다.</summary>
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

    /// <summary>제공자 요청 1건이 결과 없이 끝났다. 히트가 없을 때만 가장 늦은 완료 지연을 남긴다.</summary>
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

namespace Musebase.Core.Search;

/// <summary>一次含义查询的结果 —— 文本(如果有)，以及服务器当前是否正在生成。</summary>
public sealed record MeaningLookup(SongMeaningView? Meaning, bool Pending, int RetryAfterMs)
{
    public static readonly MeaningLookup None = new(null, false, 0);
}

/// <summary>
/// "服务器正在生成就稍等再取" —— 与获取歌词服务器翻译相同的流程，也用于含义。
///
/// 新上传的歌曲由服务器自动生成含义(数秒)。若应用在此期间询问，会返回 404 并带上 <c>pending</c>，
/// 这里按 <c>retryAfterMs</c> 间隔再次询问。服务器没有给出提示时(歌曲以无结果·资料不足
/// 结束、旧版服务器)，问一次就结束 —— 不给"等也没用"的歌曲堆积请求。
///
/// <b>await 上不使用 ConfigureAwait(false)</b> —— <paramref name="onPending"/> 会修改界面，
/// 因此必须在 UI 线程调用(WPF 与 Android 皆然)。
/// </summary>
public static class MeaningFetch
{
    /// <summary>等待上限(ms)。服务器生成通常 2~5 秒，这个余量足够。</summary>
    public const int DefaultMaxWaitMs = 20_000;

    public static async Task<SongMeaningView?> AwaitAsync(
        IRemoteLyricsCache remote, string title, string artist,
        Action? onPending = null, int maxWaitMs = DefaultMaxWaitMs, CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(maxWaitMs);
        var announced = false;
        while (true)
        {
            var got = await remote.LookupMeaningAsync(title, artist, ct);
            if (got.Meaning is not null || !got.Pending || ct.IsCancellationRequested) return got.Meaning;

            if (!announced)
            {
                announced = true;
                onPending?.Invoke();
            }

            var wait = Math.Clamp(got.RetryAfterMs, 1000, 5000);
            if (DateTimeOffset.UtcNow.AddMilliseconds(wait) > deadline) return null;
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return null; }
        }
    }
}

using System.Net;
using System.Runtime.CompilerServices;

namespace Musebase.Core.Search;

/// <summary>歌词提供者通用接口。LyricsKit 的 LyricsProvider 移植版。</summary>
public interface ILyricsProvider
{
    string ServiceName { get; }

    /// <summary>搜索 → 按候选获取歌词。按到达顺序流式产出。</summary>
    IAsyncEnumerable<Lyrics> GetLyricsAsync(LyricsSearchRequest request, CancellationToken ct = default);
}

/// <summary>
/// 基于令牌(搜索结果条目)的提供者基类。LyricsKit 的 _LyricsProvider 移植版。
/// 搜索失败返回空流，单个候选获取失败只跳过该项。
/// </summary>
public abstract class LyricsProviderBase<TToken> : ILyricsProvider
{
    public abstract string ServiceName { get; }

    protected abstract Task<IReadOnlyList<TToken>> SearchAsync(LyricsSearchRequest request, CancellationToken ct);

    protected abstract Task<Lyrics?> FetchAsync(TToken token, CancellationToken ct);

    public async IAsyncEnumerable<Lyrics> GetLyricsAsync(
        LyricsSearchRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        IReadOnlyList<TToken> tokens;
        try
        {
            tokens = await SearchAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch
        {
            // 单个提供者搜索失败不应阻断整体搜索，静默结束
            yield break;
        }

        // 候选并行获取，按搜索顺序产出
        var fetchTasks = tokens.Take(request.Limit)
            .Select(t => FetchAsync(t, ct))
            .ToList();

        foreach (var task in fetchTasks)
        {
            Lyrics? lyrics = null;
            try
            {
                lyrics = await task.ConfigureAwait(false);
            }
            catch
            {
                // 单个候选失败则跳过
            }
            if (lyrics is null) continue;

            lyrics.Metadata.ServiceName = ServiceName;
            lyrics.Metadata.Request = request;
            yield return lyrics;
        }
    }
}

/// <summary>提供者共用的 HttpClient (连接复用)</summary>
public static class LyricsHttp
{
    public static readonly HttpClient Client = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false, // Cookie 由各提供者手动管理 (NetEase 2-pass)
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
}

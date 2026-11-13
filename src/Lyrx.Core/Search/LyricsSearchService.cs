using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Musebase.Core.Search;

/// <summary>
/// 多提供者并行搜索聚合。对应 LyricsKit 的 Provider/Group.swift。
/// 同时运行所有提供者，按到达顺序流式输出结果，
/// 或按质量排序后返回。
/// </summary>
public sealed class LyricsSearchService
{
    private readonly IReadOnlyList<ILyricsProvider> _providers;

    public LyricsSearchService(params ILyricsProvider[] providers)
    {
        _providers = providers.Length > 0
            ? providers
            : [new LrclibProvider(), new NetEaseProvider(), new KugouProvider(), new QQMusicProvider()];
    }

    /// <summary>
    /// 按到达顺序流式输出所有提供者的结果（提供者之间顺序不保证）。
    /// 除原始搜索词外，还搜索清理后的变体（去掉 featuring/重制版等）以扩大覆盖范围，
    /// 并按（提供者，歌曲 token）去掉重复结果。
    /// 传入 <paramref name="diagnostics"/> 时按来源统计命中与延迟（供遥测使用，不含歌曲信息）。
    /// </summary>
    public async IAsyncEnumerable<Lyrics> SearchAsync(
        LyricsSearchRequest request,
        [EnumeratorCancellation] CancellationToken ct = default,
        SearchDiagnostics? diagnostics = null)
    {
        var requests = new List<LyricsSearchRequest> { request };
        foreach (var variant in SearchTermCleaner.Variants(request.Term))
            requests.Add(request with { Term = variant });

        var channel = Channel.CreateUnbounded<Lyrics>();
        var seen = new HashSet<string>();
        var seenLock = new object();

        async Task ProcessAsync(ILyricsProvider provider, LyricsSearchRequest req)
        {
            var sw = diagnostics is null ? null : Stopwatch.StartNew();
            var hit = false;
            try
            {
                await foreach (var lyrics in provider.GetLyricsAsync(req, ct).ConfigureAwait(false))
                {
                    if (!hit)
                    {
                        hit = true; // 去重之前判定 —— 记录提供者确实产出了结果这一事实本身
                        diagnostics?.ReportHit(provider.ServiceName, sw!.ElapsedMilliseconds);
                    }
                    bool fresh;
                    lock (seenLock) fresh = seen.Add(DedupKey(lyrics));
                    if (fresh) await channel.Writer.WriteAsync(lyrics, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 取消时静默处理
            }
            finally
            {
                if (!hit) diagnostics?.ReportMiss(provider.ServiceName, sw?.ElapsedMilliseconds ?? 0);
            }
        }

        var workers = (from provider in _providers
                       from req in requests
                       select ProcessAsync(provider, req)).ToArray();

        _ = Task.WhenAll(workers).ContinueWith(
            _ => channel.Writer.TryComplete(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (var lyrics in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return lyrics;
    }

    /// <summary>去重键：同一提供者的同一首歌（token）即视为相同。没有 token 时改用标题+首行。</summary>
    private static string DedupKey(Lyrics lyrics)
    {
        var service = lyrics.Metadata.ServiceName ?? "?";
        if (!string.IsNullOrEmpty(lyrics.Metadata.ServiceToken))
            return $"{service}|{lyrics.Metadata.ServiceToken}";

        var title = lyrics.IdTags.GetValueOrDefault(Lyrics.TagTitle) ?? "";
        var first = lyrics.Lines.Count > 0 ? lyrics.Lines[0].Content : "";
        return $"{service}|{title}|{first}";
    }

    /// <summary>收集全部结果，按质量降序返回</summary>
    public async Task<List<Lyrics>> SearchAllAsync(LyricsSearchRequest request, CancellationToken ct = default)
    {
        var results = new List<Lyrics>();
        await foreach (var lyrics in SearchAsync(request, ct).ConfigureAwait(false))
            results.Add(lyrics);
        return results.OrderByDescending(l => l.Quality()).ToList();
    }

    /// <summary>质量最高的 1 个候选（没有则为 null）</summary>
    public async Task<Lyrics?> SearchBestAsync(LyricsSearchRequest request, CancellationToken ct = default)
    {
        var all = await SearchAllAsync(request, ct).ConfigureAwait(false);
        return all.FirstOrDefault();
    }
}

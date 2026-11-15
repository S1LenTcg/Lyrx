using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Musebase.Core.Search;

/// <summary>
/// <see cref="IRemoteLyricsCache"/> 的 HTTP 实现 —— 个人歌词服务器（`contracts/lyrics-api.md` v1）。
///
/// 三条设计原则：
/// 1. **静默降级** —— 吞掉所有异常，查询返回 null，保存一律忽略。服务器挂了应用也照常工作。
/// 2. **短超时** —— 查询默认 2.5 秒。即使失败，之后仍会继续走提供方搜索，所以
///    在这里拖太久就会变成“开了服务器反而变慢”。
/// 3. **熔断器** —— 连续失败累积到一定程度后，一段时间内完全不尝试。
///    避免在 Tailscale 之外（Tailscale 关闭）时每次换歌都要等超时的浪费。
/// </summary>
public sealed class HttpRemoteLyricsCache : IRemoteLyricsCache
{
    /// <summary>连续失败达到该次数时打开熔断。</summary>
    private const int FailureThreshold = 2;
    private static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(60);
    /// <summary>上传在后台进行，因此比查询留出更多余量。</summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 为避免套接字耗尽，每个实例各保留一个（配置变更时会新建实例）。
    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;

    private int _consecutiveFailures;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public HttpRemoteLyricsCache(string endpoint, string? token, int timeoutMs = 2500, Action<string>? log = null)
        : this(endpoint, token, timeoutMs, log, handler: null) { }

    /// <summary>用于在测试中替换传输层的构造函数（注入桩处理器）。</summary>
    internal HttpRemoteLyricsCache(
        string endpoint, string? token, int timeoutMs, Action<string>? log, HttpMessageHandler? handler)
    {
        var normalized = endpoint.TrimEnd('/');
        _baseUri = new Uri(normalized + "/", UriKind.Absolute);
        _timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 500, 30_000));
        _log = log;

        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(1.5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            // 实际过期时间由每个请求各自链接的 CTS 控制（查询/上传的时长不同）。
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Trim());
    }

    public async Task<RemoteLyricsResult> GetAsync(string title, string artist, CancellationToken ct = default)
    {
        if (IsCircuitOpen()) return RemoteLyricsResult.Miss;

        try
        {
            var url = new Uri(_baseUri, $"v1/lyrics?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                OnSuccess(); // 未命中也是正常响应
                return await ReadMissAsync(response, cts.Token).ConfigureAwait(false);
            }
            if (!response.IsSuccessStatusCode)
            {
                OnFailure($"HTTP {(int)response.StatusCode}");
                return RemoteLyricsResult.Miss;
            }

            var entry = await response.Content.ReadFromJsonAsync<RemoteLyricsEntry>(Json, cts.Token).ConfigureAwait(false);
            OnSuccess();
            if (entry?.Lrc is not { Length: > 0 } lrc) return RemoteLyricsResult.Miss;

            var lyrics = Lyrics.Parse(lrc);
            if (lyrics is null) return RemoteLyricsResult.Miss;
            lyrics.Metadata.ServiceName = entry.Service ?? "Server";
            var langs = entry.Langs ?? [];
            _log?.Invoke($"[server] 命中：{title} — {entry.Service} (match={entry.Match}, langs={string.Join(",", langs)})");
            return new RemoteLyricsResult(lyrics, false, 0, langs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return RemoteLyricsResult.Miss; // 换曲等场景 —— 不计入失败
        }
        catch (Exception e)
        {
            OnFailure(e.Message);
            return RemoteLyricsResult.Miss;
        }
    }

    /// <summary>
    /// 读取 404 响应体中的让位提示。响应体缺失或损坏时，按普通未命中处理 ——
    /// 与旧版服务器（无响应体的 404）保持兼容。
    /// </summary>
    private async Task<RemoteLyricsResult> ReadMissAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<MissBody>(Json, ct).ConfigureAwait(false);
            if (body is { Ad: true })
            {
                _log?.Invoke("[server] 该标题被标记为广告 —— 不查找歌词");
                return RemoteLyricsResult.Ad;
            }
            if (body is not { Pending: true }) return RemoteLyricsResult.Miss;
            _log?.Invoke($"[server] 其他设备也在查找这首歌 —— 让出翻译({body.RetryAfterMs}ms)");
            return new RemoteLyricsResult(null, true, body.RetryAfterMs, []);
        }
        catch (Exception)
        {
            return RemoteLyricsResult.Miss;
        }
    }

    /// <summary>
    /// 歌曲含义。服务器上没有就返回 404，这是正常的 —— 大多数歌曲暂时还没有含义。
    ///
    /// 熔断器与失败计数**与歌词共用**：这属于附加信息，若因这里失败就
    /// 连歌词查询也一并阻断，损失太大。所以失败时也不调用 <see cref="OnFailure"/>，
    /// 只静默返回 null（熔断已打开时则完全不尝试）。
    /// </summary>
    public async Task<SongMeaningView?> GetMeaningAsync(
        string title, string artist, CancellationToken ct = default) =>
        (await LookupMeaningAsync(title, artist, ct).ConfigureAwait(false)).Meaning;

    public async Task<MeaningLookup> LookupMeaningAsync(
        string title, string artist, CancellationToken ct = default)
    {
        if (IsCircuitOpen()) return MeaningLookup.None;

        try
        {
            var url = new Uri(_baseUri,
                $"v1/meaning?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 = 还没有含义（正常）。若服务器正在生成，响应体里会带 pending。
                if (response.StatusCode != HttpStatusCode.NotFound) return MeaningLookup.None;
                try
                {
                    var miss = await response.Content.ReadFromJsonAsync<MissBody>(Json, cts.Token).ConfigureAwait(false);
                    return miss is { Pending: true }
                        ? new MeaningLookup(null, true, miss.RetryAfterMs)
                        : MeaningLookup.None;
                }
                catch (Exception)
                {
                    return MeaningLookup.None;   // 旧版服务器 —— 无响应体的 404
                }
            }

            var entry = await response.Content
                .ReadFromJsonAsync<RemoteMeaningEntry>(Json, cts.Token).ConfigureAwait(false);
            return new MeaningLookup(ToView(entry), false, 0);
        }
        catch (Exception)
        {
            return MeaningLookup.None; // 附加功能 —— 不影响歌词查询
        }
    }

    /// <summary>
    /// 请求现在生成含义（仅在人工点击按钮时调用）。
    ///
    /// 与查询不同，<b>即使熔断已打开也会尝试</b> —— 这是人刚触发的操作，若静默地
    /// 什么都不发生，看起来就像故障。作为代价，结果会如实返回失败。
    /// 超时也单独设得更长：要收集外部资料 + 调用 LLM，会花上数十秒。
    /// </summary>
    public async Task<MeaningRequestResult> RequestMeaningAsync(
        string title, string artist, bool force = false, CancellationToken ct = default)
    {
        try
        {
            // 没有 force 时，原样返回服务器已生成的内容 —— 不覆盖已有文本。
            var url = new Uri(_baseUri,
                $"v1/meaning?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist)}"
                + (force ? "&force=1" : ""));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(GenerateTimeout);

            using var response = await _http.PostAsync(url, null, cts.Token).ConfigureAwait(false);

            // 403（应用生成已关闭） · 503（引擎未配置） —— 两者都表示“这台服务器上做不了”。
            if (response.StatusCode is System.Net.HttpStatusCode.Forbidden
                                    or System.Net.HttpStatusCode.ServiceUnavailable)
                return MeaningRequestResult.Of(MeaningRequestStatus.Unavailable);

            if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                var notMade = await response.Content
                    .ReadFromJsonAsync<RemoteMeaningNotMade>(Json, cts.Token).ConfigureAwait(false);
                return MeaningRequestResult.Of(notMade?.Status switch
                {
                    "no-source" => MeaningRequestStatus.NoSource,
                    "insufficient" => MeaningRequestStatus.Insufficient,
                    "retry" => MeaningRequestStatus.Retry,
                    // 服务器引擎的计费·密钥·模型问题 —— 应用侧无能为力，只能修服务器。
                    "config" => MeaningRequestStatus.Unavailable,
                    _ => MeaningRequestStatus.Failed,
                });
            }

            if (!response.IsSuccessStatusCode) return MeaningRequestResult.Of(MeaningRequestStatus.Failed);

            var entry = await response.Content
                .ReadFromJsonAsync<RemoteMeaningEntry>(Json, cts.Token).ConfigureAwait(false);
            var view = ToView(entry);
            return view is null
                ? MeaningRequestResult.Of(MeaningRequestStatus.Failed)
                : new MeaningRequestResult(MeaningRequestStatus.Created, view);
        }
        catch (Exception)
        {
            return MeaningRequestResult.Of(MeaningRequestStatus.Failed);
        }
    }

    /// <summary>生成要收集外部资料 + 调用 LLM，用查询超时（数秒）必然会被截断。</summary>
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromSeconds(90);

    // ---- 歌曲附属信息（封面·喜欢） ----

    /// <summary>
    /// 若服务器还没为这首歌找过封面，它那边会调用一次外部 API，因此比歌词查询更慢。
    /// 所以<b>不使用与歌词相同的超时</b>，而是留足余量，但失败时静默返回 null。
    /// 这属于附加信息，不计入熔断器 —— 若因此把歌词卡住，损失太大。
    /// </summary>
    public async Task<SongExtrasResult> GetExtrasAsync(
        string title, string artist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return SongExtrasResult.NotFound;
        try
        {
            using var response = await SendExtrasAsync(
                HttpMethod.Get, "v1/song", title, artist, null, ct).ConfigureAwait(false);

            // 404 只表示“服务器不认识这首歌” —— 若是尚无人上传的新歌，这是正常的，
            // 几秒后本机会上传。若按错误上报，每首新歌都会弹警告。
            if (response.StatusCode == HttpStatusCode.NotFound) return SongExtrasResult.NotFound;
            if (!response.IsSuccessStatusCode) return SongExtrasResult.Failed;

            var extras = await ReadExtrasAsync(response, ct).ConfigureAwait(false);
            return extras is null ? SongExtrasResult.Failed : SongExtrasResult.Ok(extras);
        }
        catch (Exception)
        {
            return SongExtrasResult.Failed;   // 地址·网络·超时 —— 这些必须告知
        }
    }

    public Task<SongExtras?> RefreshCoverAsync(string title, string artist, CancellationToken ct = default) =>
        ExtrasAsync(HttpMethod.Post, "v1/song/cover", title, artist, null, ct);

    public Task<SongExtras?> SetLovedAsync(
        string title, string artist, bool loved, CancellationToken ct = default) =>
        ExtrasAsync(HttpMethod.Post, "v1/song/love", title, artist, loved ? "1" : "0", ct);

    /// <summary>发一次请求。调用方需要查看状态码，因此原样返回响应。</summary>
    private async Task<HttpResponseMessage> SendExtrasAsync(
        HttpMethod method, string path, string title, string artist, string? on, CancellationToken ct)
    {
        var query = $"?title={Uri.EscapeDataString(title)}&artist={Uri.EscapeDataString(artist ?? "")}"
                  + (on is null ? "" : $"&on={on}");
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path + query));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ExtrasTimeout);

        return await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
    }

    private async Task<SongExtras?> ExtrasAsync(
        HttpMethod method, string path, string title, string artist, string? on, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            using var response = await SendExtrasAsync(method, path, title, artist, on, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await ReadExtrasAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null; // 附加信息 —— 不影响歌词查询
        }
    }

    /// <summary>把成功响应的响应体转换为 <see cref="SongExtras"/>。响应体为空时返回 null。</summary>
    private static async Task<SongExtras?> ReadExtrasAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ExtrasTimeout);

        var body = await response.Content
            .ReadFromJsonAsync<RemoteSongExtras>(Json, cts.Token).ConfigureAwait(false);

        return body is null
            ? null
            : new SongExtras(
                string.IsNullOrWhiteSpace(body.CoverUrl) ? null : body.CoverUrl,
                body.CoverSource, body.LastFmUrl,
                body.LoveConnected, body.LoveKnown, body.Loved,
                string.IsNullOrWhiteSpace(body.Key) ? null : body.Key,
                body.SpotifyConnected, body.SpotifyKnown, body.SpotifySaved);
    }

    /// <summary>若服务器是第一次为这首歌查封面，会插入一次外部 API 调用（iTunes 2.5 秒 + 余量）。</summary>
    private static readonly TimeSpan ExtrasTimeout = TimeSpan.FromSeconds(12);

    private sealed record RemoteSongExtras
    {
        public string? CoverUrl { get; init; }
        public string? CoverSource { get; init; }
        public string? LastFmUrl { get; init; }
        public bool LoveConnected { get; init; }
        public bool LoveKnown { get; init; }
        public bool Loved { get; init; }

        /// <summary>旧版服务器不发送该字段 —— 此时为 null，只有管理界面的按钮会消失。</summary>
        public string? Key { get; init; }

        // 旧版服务器不发送 —— 这些会全为 false，效果等同于完全没有 Spotify 显示。
        public bool SpotifyConnected { get; init; }
        public bool SpotifyKnown { get; init; }
        public bool SpotifySaved { get; init; }
    }

    public async Task SetAsync(string title, string artist, Lyrics lyrics, CancellationToken ct = default)
    {
        if (IsCircuitOpen()) return;

        try
        {
            var service = lyrics.Metadata.ServiceName;
            var payload = new RemoteLyricsEntry
            {
                Title = title,
                Artist = artist,
                Lrc = lyrics.ToString(),
                Service = service,
                // 用户编辑版在服务器上受保护，不会被自动搜索结果覆盖。
                Origin = (service == EditedServiceName || service == LegacyEditedServiceName) ? "user" : "provider",
            };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(UploadTimeout);
            using var response = await _http.PutAsJsonAsync(new Uri(_baseUri, "v1/lyrics"), payload, Json, cts.Token)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode) OnSuccess();
            else OnFailure($"PUT HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 忽略
        }
        catch (Exception e)
        {
            OnFailure(e.Message);
        }
    }

    /// <summary>用户编辑版标记 —— 必须与 <c>LyricsCoordinator.SaveEditedLyrics</c> 写入的值一致。</summary>
    public const string EditedServiceName = "用户编辑";
    private const string LegacyEditedServiceName = "\uC0AC\uC6A9\uC790 \uD3B8\uC9D1";

    // ---- 熔断器 ----

    private bool IsCircuitOpen()
    {
        if (DateTimeOffset.UtcNow >= _openUntil) return false;
        return true;
    }

    private void OnSuccess()
    {
        _consecutiveFailures = 0;
        _openUntil = DateTimeOffset.MinValue;
    }

    private void OnFailure(string reason)
    {
        _consecutiveFailures++;
        if (_consecutiveFailures >= FailureThreshold)
        {
            _openUntil = DateTimeOffset.UtcNow + OpenDuration;
            _log?.Invoke($"[server] 连续失败 {_consecutiveFailures} 次 —— {(int)OpenDuration.TotalSeconds} 秒内跳过查询 ({reason})");
        }
        else
        {
            _log?.Invoke($"[server] 失败：{reason}");
        }
    }

    /// <summary>服务器响应/请求体（契约 v1，camelCase）。</summary>
    private sealed record RemoteLyricsEntry
    {
        public string? Key { get; init; }
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string Lrc { get; init; } = "";
        public string? Service { get; init; }
        public string Origin { get; init; } = "provider";
        public string[]? Langs { get; init; }
        public int? LineCount { get; init; }
        public bool? HasInlineTimeTags { get; init; }
        public int? Revision { get; init; }
        public string? UpdatedAt { get; init; }
        public string? Match { get; init; }
    }

    /// <summary>`GET /v1/meaning` 的响应（仅需要的字段）。</summary>
    /// <summary>
    /// 响应 → 界面上展示的形态。查询与生成都会返回<b>同样结构</b>的响应体，因此集中在一处转换。
    /// 标注来源是必须的，因此与响应体一并返回（空名称会被丢弃）。
    /// </summary>
    private static SongMeaningView? ToView(RemoteMeaningEntry? entry)
    {
        if (entry?.Summary is not { Length: > 0 } summary) return null;

        var credits = (entry.Attribution ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .Select(a => new MeaningCredit(a.Name!, a.Url))
            .ToList();
        return new SongMeaningView(summary.Trim(), credits, entry.Lang ?? "ko");
    }

    private sealed record RemoteMeaningEntry
    {
        public string? Summary { get; init; }
        public string? Lang { get; init; }
        public RemoteAttribution[]? Attribution { get; init; }
    }

    /// <summary>生成以 202 结束时的响应体 —— 其中包含未能生成的原因。</summary>
    private sealed record RemoteMeaningNotMade
    {
        public string? Status { get; init; }
    }

    private sealed record RemoteAttribution
    {
        public string? Name { get; init; }
        public string? Url { get; init; }
    }

    /// <summary>404 响应体（契约 v1 的“让出翻译”·“广告拦截”）。旧版服务器没有响应体。</summary>
    private sealed record MissBody
    {
        /// <summary>该标题被标记为广告 —— 既不进行提供方搜索也不上传。</summary>
        public bool Ad { get; init; }

        public string? Error { get; init; }
        public bool Pending { get; init; }
        public int RetryAfterMs { get; init; }
    }
}

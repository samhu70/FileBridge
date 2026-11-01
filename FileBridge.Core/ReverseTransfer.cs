using System.Net;
using System.Text.Json;

namespace FileBridge.Core;

/// <summary>Download explicitly offered files using outgoing HTTPS connections only; no local listener is opened.</summary>
public static class ReverseTransfer
{
    /// <summary>Receive selected remote files with durable resume, per-chunk and whole-file SHA-256 validation.</summary>
    /// <param name="code">The reachable sender's pinned, authenticated connection code.</param>
    /// <param name="directory">Local destination; existing different files and unsafe paths are protected.</param>
    /// <param name="progress">Receiving, verification and reconnection progress.</param>
    /// <param name="ct">Pauses downloading while preserving verified partial files.</param>
    /// <param name="parallelChunks">Receiver safety cap, from one to ten; defaults to allowing the sender's selected limit.</param>
    public static async Task ReceiveAsync(string code, string directory, IProgress<TransferProgress>? progress,
        CancellationToken ct, int parallelChunks = Wire.MaxParallelChunks)
    {
        if (parallelChunks is < 1 or > Wire.MaxParallelChunks) throw new ArgumentOutOfRangeException(nameof(parallelChunks));
        using var client = TransferSender.CreateClient(Invitation.Parse(code));
        var reporting = new ReceivingProgress(progress);
        var offer = await ReadReadyOfferAsync(client, reporting, null, ct);
        await using var local = TransferReceiver.CreateLocal(directory, reporting);
        foreach (var manifest in offer.Files)
        {
            for (var attempt = 0; ; attempt = Math.Min(attempt + 1, 4))
            {
                try
                {
                    if (attempt > 0)
                    {
                        offer = await ReadReadyOfferAsync(client, reporting, manifest, ct);
                        if (!offer.Files.Any(f => f.Id == manifest.Id))
                            throw new InvalidDataException("反向发送任务已变化，请重新连接。");
                    }
                    var streams = TransferSender.StreamsForSize(manifest.Size,
                        TransferSender.NegotiatedStreams(parallelChunks, offer.ParallelChunks));
                    await DownloadAsync(client, offer.Id, manifest, local, streams, offer.ChunkSize, reporting, ct);
                    break;
                }
                catch (Exception error) when (CanReconnect(error, ct))
                {
                    reporting.Wait(manifest, OfferUnavailable(error));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << attempt, 15)), ct);
                }
            }
        }
    }

    // Keep the original manifests: a new offer ID may resume them, but cannot silently substitute other files.
    private sealed class ReceivingProgress(IProgress<TransferProgress>? target) : IProgress<TransferProgress>
    {
        private TransferProgress? last;
        public void Report(TransferProgress value) { last = value; target?.Report(value); }
        public void Wait(FileManifest? manifest, bool senderPaused)
        {
            var previous = last?.FileName == manifest?.Name ? last : null;
            Report((previous ?? new TransferProgress(manifest?.Name ?? "", TransferStage.WaitingForSender, Total: manifest?.Size ?? 0))
                with { Stage = senderPaused ? TransferStage.WaitingForSender : TransferStage.Retrying,
                    BytesPerSecond = 0, ParallelChunks = 0, RetryAttempt = 0, RetryLimit = 0 });
        }
    }

    private static bool OfferUnavailable(Exception error) => error is TransferException { Status: 409 } e
        && e.Message is "对方尚未准备反向发送文件，请让对方准备后重试。" or "反向发送任务已变化，请重新连接。";

    private static bool CanReconnect(Exception error, CancellationToken ct) => !ct.IsCancellationRequested
        && (OfferUnavailable(error) || TransferSender.Retryable(error, ct) || error is OperationCanceledException or HttpIOException);

    private static async Task<ReverseOffer> ReadReadyOfferAsync(HttpClient client, ReceivingProgress progress,
        FileManifest? manifest, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt = Math.Min(attempt + 1, 4))
        {
            try { return await ReadOfferAsync(client, ct); }
            catch (Exception error) when (CanReconnect(error, ct))
            {
                progress.Wait(manifest, OfferUnavailable(error));
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << attempt, 15)), ct);
            }
        }
    }

    private static async Task<ReverseOffer> ReadOfferAsync(HttpClient client, CancellationToken ct)
    {
        var offer = await PostJsonAsync<ReverseOffer>(client, "v3/reverse/list", ct, 16 * 1024 * 1024);
        if (!Wire.ValidHash(offer.Id) || offer.ParallelChunks is < 1 or > Wire.MaxParallelChunks
            || offer.ChunkSize is < Wire.ReverseChunkSize or > Wire.ChunkSize
            || offer.Files is null || offer.Files.Length == 0)
            throw new InvalidDataException("反向发送的文件列表无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in offer.Files)
            if (file is null || !Wire.ValidName(file.Name) || !Wire.ValidHash(file.Hash) || file.Size < 0 || !names.Add(file.Name))
                throw new InvalidDataException("反向发送的文件列表无效。");
        return offer;
    }

    private static async Task DownloadAsync(HttpClient client, string offer, FileManifest manifest, TransferReceiver local,
        int streams, int chunkSize, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var state = await local.BeginLocalAsync(manifest, streams, false, ct);
        var query = $"?offer={offer}&id={manifest.Id}&parallelChunks={streams}";
        if (!state.Done)
        {
            if (state.Offset > 0)
            {
                var prefix = await PostJsonAsync<ResumeState>(client, "v3/reverse/prefix" + query + "&length=" + state.Offset, ct);
                if (prefix.Offset != state.Offset || !Wire.ValidHash(prefix.Prefix)) throw new InvalidDataException("接收端进度无效。");
                if (prefix.Prefix != state.Prefix)
                {
                    progress?.Report(new(manifest.Name, TransferStage.ResendingCorrupt, Total: manifest.Size, ParallelChunks: streams));
                    state = await local.BeginLocalAsync(manifest, streams, true, ct);
                }
            }
            var reportRead = await local.TrackLocalReadAsync(manifest.Id, ct);
            using var downloading = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = new List<Task>(streams);
            Exception? failure = null;
            try
            {
                var offset = state.Offset;
                while (offset < manifest.Size)
                {
                    var count = (int)Math.Min(chunkSize, manifest.Size - offset);
                    pending.Add(DownloadChunkAsync(offset, count));
                    offset += count;
                    if (pending.Count == streams)
                    {
                        await Task.WhenAll(pending);
                        pending.Clear();
                    }
                }
                await Task.WhenAll(pending);
            }
            catch
            {
                downloading.Cancel();
                try { await Task.WhenAll(pending); } catch { }
                if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }

            async Task DownloadChunkAsync(long offset, int count)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(downloading.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    using var request = new HttpRequestMessage(HttpMethod.Post, "v3/reverse/chunk" + query + "&offset=" + offset + "&length=" + count);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    await CheckResponseAsync(response, false, timeout.Token);
                    if (response.Content.Headers.ContentLength != count
                        || !response.Headers.TryGetValues("X-Chunk-SHA256", out var values))
                        throw new InvalidDataException("数据块大小无效。");
                    var hash = values.SingleOrDefault();
                    if (!Wire.ValidHash(hash)) throw new InvalidDataException("数据块校验失败，未写入。");
                    // Headers have arrived: the body is bounded by inactivity, not its total transfer duration.
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await using var body = await response.Content.ReadAsStreamAsync(downloading.Token);
                    var bytes = new byte[count];
                    try { await ReadChunkAsync(body, bytes, reportRead, TimeSpan.FromMinutes(5), downloading.Token); }
                    catch (IOException error) { throw new HttpRequestException("接收连接中断。", error); }
                    await local.WriteLocalAsync(manifest.Id, offset, bytes, hash!, downloading.Token, reportBytes: false);
                }
                catch (Exception error)
                { Interlocked.CompareExchange(ref failure, error, null); downloading.Cancel(); throw; }
            }
            await local.FinishLocalAsync(manifest.Id, ct);
        }
        // A completion receipt is sent only after the local final file passes SHA-256 and atomic publication.
        // Retry this receipt against the same offer: a completed sender has already withdrawn its file list.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var receipt = await PostJsonAsync<ResumeState>(client, "v3/reverse/complete" + query, ct);
                if (!receipt.Done || receipt.Offset != manifest.Size) throw new InvalidDataException("接收端确认进度不一致。");
                break;
            }
            catch (Exception error) when (attempt < 9 && TransferSender.Retryable(error, ct))
            { await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << Math.Min(attempt, 4), 15)), ct); }
        }
    }

    internal static async Task ReadChunkAsync(Stream body, byte[] bytes, Action<int> reportRead, TimeSpan idleTimeout,
        CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var read = 0;
        try
        {
            while (read < bytes.Length)
            {
                idle.CancelAfter(idleTimeout);
                var count = await body.ReadAsync(bytes.AsMemory(read), idle.Token);
                if (count == 0) throw new EndOfStreamException();
                read += count;
                reportRead(count);
            }
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        { throw new TransferException(408, "接收数据等待超时，请重新连接。", error); }
    }

    private static async Task<T> PostJsonAsync<T>(HttpClient client, string path, CancellationToken ct, int limit = 16384)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await CheckResponseAsync(response, path == "v3/reverse/list", timeout.Token);
        return JsonSerializer.Deserialize<T>(await ReadBoundedAsync(response.Content, limit, timeout.Token), JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("反向发送的文件列表无效。");
    }

    private static async Task CheckResponseAsync(HttpResponseMessage response, bool listing, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (listing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new InvalidDataException("对方版本不支持反向传输，请升级双方客户端。");
        var message = System.Text.Encoding.UTF8.GetString(await ReadBoundedAsync(response.Content, 16384, ct));
        throw new TransferException((int)response.StatusCode, message);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("反向发送的文件列表过大，请分批选择。");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int count;
            try { count = await stream.ReadAsync(buffer, ct); }
            catch (IOException error) { throw new HttpRequestException("接收连接中断。", error); }
            if (count == 0) break;
            if (result.Length + count > limit) throw new InvalidDataException("反向发送的文件列表过大，请分批选择。");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }
}

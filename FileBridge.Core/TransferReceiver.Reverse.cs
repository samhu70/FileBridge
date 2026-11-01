using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace FileBridge.Core;

public sealed partial class TransferReceiver
{
    private OutgoingOffer? outgoing;
    private ReverseOffer? completedOffer;

    private sealed record OfferedFile(string Path, DateTime LastWrite, FileManifest Manifest);
    private sealed class OutgoingOffer(ReverseOffer offer, Dictionary<string, OfferedFile> files,
        IProgress<TransferProgress>? progress, CancellationToken cancellation)
    {
        public ReverseOffer Offer { get; } = offer;
        public Dictionary<string, OfferedFile> Files { get; } = files;
        public IProgress<TransferProgress>? Progress { get; } = progress;
        public CancellationToken Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HashSet<string> Completed { get; } = [];
        public Dictionary<string, (long Initial, long Confirmed, Stopwatch Clock)> Sent { get; } = [];
        public object Sync { get; } = new();
    }

    /// <summary>Offer only selected files on this listener until the peer verifies them or the sender cancels.</summary>
    /// <param name="files">Local source files explicitly selected by the user.</param>
    /// <param name="progress">Hashing, sending and peer-verified completion notifications.</param>
    /// <param name="ct">Withdraws the offer; already verified receiving prefixes remain resumable.</param>
    /// <param name="parallelChunks">Per-file concurrent request limit, from one to ten.</param>
    public async Task SendReverseAsync(IReadOnlyList<TransferFile> files, IProgress<TransferProgress>? progress,
        CancellationToken ct, int parallelChunks = Wire.DefaultParallelChunks)
    {
        if (app is null) throw new InvalidOperationException("请先在接收页启动本机接收服务，再反向发送。");
        if (parallelChunks is < 1 or > Wire.MaxParallelChunks) throw new ArgumentOutOfRangeException(nameof(parallelChunks));
        if (files.Count == 0) throw new InvalidDataException("请先选择文件或文件夹。");
        var selected = new Dictionary<string, OfferedFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!Wire.ValidName(file.RelativeName) || !names.Add(file.RelativeName)) throw new InvalidDataException("文件信息无效。");
            var before = new FileInfo(file.LocalPath);
            if ((before.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("文件信息无效。");
            var size = before.Length;
            var modified = before.LastWriteTimeUtc;
            progress?.Report(new(file.RelativeName, TransferStage.CalculatingHash, Total: size, ParallelChunks: 0));
            var manifest = new FileManifest(file.RelativeName, size, await Wire.FileHashAsync(file.LocalPath, ct));
            var entry = new OfferedFile(Path.GetFullPath(file.LocalPath), modified, manifest);
            CheckSource(entry);
            selected.Add(manifest.Id, entry);
        }
        var descriptor = new ReverseOffer(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)),
            selected.Values.Select(f => f.Manifest).ToArray(), parallelChunks);
        if (JsonSerializer.SerializeToUtf8Bytes(descriptor).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("反向发送的文件列表过大，请分批选择。");
        var offer = new OutgoingOffer(descriptor, selected, progress, ct);
        if (Interlocked.CompareExchange(ref outgoing, offer, null) is not null)
            throw new InvalidOperationException("已有反向发送任务，请先暂停该任务。");
        try
        {
            if (app is null) throw new IOException("反向发送服务已停止，请重新开始。");
            progress?.Report(new("", TransferStage.WaitingForData, ParallelChunks: 0));
            await offer.Completion.Task.WaitAsync(ct);
        }
        finally { Interlocked.CompareExchange(ref outgoing, null, offer); }
    }

    private static void CheckSource(OfferedFile file)
    {
        var current = new FileInfo(file.Path);
        if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0
            || current.Length != file.Manifest.Size || current.LastWriteTimeUtc != file.LastWrite)
            throw new IOException("源文件发生变化，请停止编辑后重新发送。");
    }

    private static void CheckOfferedSource(OutgoingOffer offer, OfferedFile file)
    {
        try { CheckSource(file); }
        catch (IOException error) { offer.Completion.TrySetException(error); throw; }
    }

    private static FileStream OpenOfferedSource(OutgoingOffer offer, OfferedFile file)
    {
        try { return new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var failure = new IOException("源文件发生变化，请停止编辑后重新发送。", error);
            offer.Completion.TrySetException(failure);
            throw failure;
        }
    }

    /// <summary>Serve authenticated POST reverse list/prefix/chunk/complete requests; no caller-supplied local paths.</summary>
    /// <param name="context">Bearer-authenticated request already checked by the shared listener.</param>
    /// <param name="ct">Disconnected peer cancellation.</param>
    private async Task HandleReverseAsync(HttpContext context, CancellationToken ct)
    {
        var path = context.Request.Path.Value;
        var offerId = context.Request.Query["offer"].ToString();
        var id = context.Request.Query["id"].ToString();
        var offer = Volatile.Read(ref outgoing);
        if (path == "/v3/reverse/complete" && Volatile.Read(ref completedOffer) is { } receipt
            && receipt.Id == offerId && receipt.Files.FirstOrDefault(f => f.Id == id) is { } completed)
        { await context.Response.WriteAsJsonAsync(new ResumeState(completed.Size, "", true), ct); return; }
        if (offer is null || offer.Cancellation.IsCancellationRequested)
            throw new TransferException(409, "对方尚未准备反向发送文件，请让对方准备后重试。");
        if (path == "/v3/reverse/list")
        { await context.Response.WriteAsJsonAsync(offer.Offer, ct); return; }
        if (offerId != offer.Offer.Id || !offer.Files.TryGetValue(id, out var file))
            throw new TransferException(409, "反向发送任务已变化，请重新连接。");
        using var active = CancellationTokenSource.CreateLinkedTokenSource(ct, offer.Cancellation);
        ct = active.Token;
        CheckOfferedSource(offer, file);
        var streams = TransferSender.StreamsForSize(file.Manifest.Size, offer.Offer.ParallelChunks);
        if (context.Request.Query.TryGetValue("parallelChunks", out var requested))
        {
            if (!int.TryParse(requested, out var count) || count is < 1 or > Wire.MaxParallelChunks)
                throw new InvalidDataException("并发数无效。");
            streams = Math.Min(streams, count);
        }
        if (path == "/v3/reverse/complete")
        {
            lock (offer.Sync)
            {
                if (offer.Completed.Add(id)) offer.Progress?.Report(new(file.Manifest.Name,
                    TransferStage.SendComplete, file.Manifest.Size, file.Manifest.Size, ParallelChunks: streams));
                if (offer.Completed.Count == offer.Files.Count)
                {
                    Volatile.Write(ref completedOffer, offer.Offer);
                    offer.Completion.TrySetResult();
                }
            }
            await context.Response.WriteAsJsonAsync(new ResumeState(file.Manifest.Size, "", true), ct);
            return;
        }
        await using var source = OpenOfferedSource(offer, file);
        if (path == "/v3/reverse/prefix")
        {
            if (!long.TryParse(context.Request.Query["length"], out var remaining) || remaining < 0 || remaining > file.Manifest.Size)
                throw new InvalidDataException("文件偏移无效。");
            var length = remaining;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[Wire.ChunkSize];
            while (remaining > 0)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), ct);
                if (count == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            CheckOfferedSource(offer, file);
            await context.Response.WriteAsJsonAsync(new ResumeState(length, Convert.ToHexStringLower(hash.GetHashAndReset())), ct);
            return;
        }
        if (path != "/v3/reverse/chunk") throw new TransferException(404, "接口不存在。");
        if (!long.TryParse(context.Request.Query["offset"], out var offset) || offset < 0 || offset >= file.Manifest.Size)
            throw new InvalidDataException("文件偏移无效。");
        source.Position = offset;
        lock (offer.Sync)
        {
            if (offer.Sent.TryGetValue(id, out var earlier))
                offer.Sent[id] = (Math.Min(earlier.Initial, offset), earlier.Confirmed, earlier.Clock);
            else offer.Sent[id] = (offset, offset, Stopwatch.StartNew());
        }
        var bytes = new byte[(int)Math.Min(Wire.ChunkSize, file.Manifest.Size - offset)];
        await source.ReadExactlyAsync(bytes, ct);
        CheckOfferedSource(offer, file);
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers["X-Chunk-SHA256"] = Wire.Hash(bytes);
        await context.Response.Body.WriteAsync(bytes, ct);
        lock (offer.Sync)
        {
            var sent = offer.Sent[id];
            var confirmed = Math.Max(sent.Confirmed, offset + bytes.Length);
            offer.Sent[id] = (sent.Initial, confirmed, sent.Clock);
            if (!offer.Completed.Contains(id)) offer.Progress?.Report(new(file.Manifest.Name, TransferStage.Sending, confirmed,
                file.Manifest.Size, (confirmed - sent.Initial) / Math.Max(sent.Clock.Elapsed.TotalSeconds, .001), streams));
        }
    }

    internal static TransferReceiver CreateLocal(string directory, IProgress<TransferProgress>? progress) => new(directory, progress, null, localOnly: true);

    internal async Task<ResumeState> BeginLocalAsync(FileManifest manifest, int streams, bool reset, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await BeginFileAsync(manifest, streams, reset, ct); }
        finally { gate.Release(); }
    }

    internal async Task<ResumeState> WriteLocalAsync(string id, long offset, byte[] bytes, string hash, CancellationToken ct)
    {
        ReceiveSession session;
        string partial;
        await gate.WaitAsync(ct);
        try { (session, partial) = Lookup(id); }
        finally { gate.Release(); }
        session.Start();
        session.Report(progress, bytes: bytes.Length);
        return await CommitChunkAsync(session, partial, offset, bytes, hash, ct);
    }

    internal async Task<ResumeState> FinishLocalAsync(string id, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await FinishFileAsync(id, ct); }
        finally { gate.Release(); }
    }
}

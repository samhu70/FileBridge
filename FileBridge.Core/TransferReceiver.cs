using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace FileBridge.Core;

/// <summary>HTTPS receiver with persistent partial files and atomic, non-overwriting completion.</summary>
public sealed partial class TransferReceiver : IAsyncDisposable
{
    private readonly string root;
    // Guards sessions, the contiguous on-disk prefix, and progressChanged.
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim chunkSlots = new(Wire.MaxParallelChunks, Wire.MaxParallelChunks);
    private TaskCompletionSource progressChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, ReceiveSession> sessions = [];
    private readonly IProgress<TransferProgress>? progress;
    private readonly ReceiverCertificateCache? certificates;
    private readonly bool publicKeyPin;
    private WebApplication? app;
    private readonly string token;
    public Invitation Invitation { get; private set; } = null!;

    private sealed class ReceiveSession(FileManifest manifest, int parallelChunks, long offset)
    {
        public FileManifest Manifest { get; } = manifest;
        public int ParallelChunks { get; } = parallelChunks;
        private readonly Stopwatch clock = new();
        private long received, committed = offset, lastReport;
        private bool active = true;

        public void Start() { lock (clock) { if (!clock.IsRunning) clock.Start(); } }
        public void Stop() { lock (clock) active = false; }
        public void Report(IProgress<TransferProgress>? progress, int bytes = 0, long? committedOffset = null)
        {
            lock (clock)
            {
                received += bytes;
                if (committedOffset is long value) committed = value;
                if (!active || committedOffset is null && clock.ElapsedMilliseconds - lastReport < 100) return;
                lastReport = clock.ElapsedMilliseconds;
                progress?.Report(new(Manifest.Name, TransferStage.Receiving, committed, Manifest.Size,
                    received / Math.Max(clock.Elapsed.TotalSeconds, .001), ParallelChunks));
            }
        }
    }

    private TransferReceiver(string directory, IProgress<TransferProgress>? progress, ReceiverIdentity? identity, bool localOnly = false)
    {
        root = Path.GetFullPath(directory);
        this.progress = progress;
        Directory.CreateDirectory(root);
        RejectLinks(root);
        Directory.CreateDirectory(SafePath(Wire.PartsDirectory, allowParts: true));
        if (localOnly) { token = ""; return; }
        var transientIdentity = identity is null;
        identity ??= ReceiverIdentity.Create();
        token = identity.Token;
        publicKeyPin = identity.PublicKeyPin;
        try { certificates = new ReceiverCertificateCache(identity); }
        finally { if (transientIdentity) CryptographicOperations.ZeroMemory(identity.Pfx); }
    }

    /// <summary>Bind the selected local IP, reusing supplied local credentials when present.</summary>
    /// <param name="address">Concrete receiver IP, normally its Tailscale address.</param>
    /// <param name="port">TCP port; zero is allowed for isolated tests.</param>
    /// <param name="directory">Trusted local directory for final and partial files.</param>
    /// <param name="progress">Optional UI progress callback.</param>
    /// <param name="ct">Startup cancellation.</param>
    /// <param name="identity">Optional persisted local credentials; omitted callers keep fresh invitations.</param>
    public static async Task<TransferReceiver> StartAsync(string address, int port, string directory,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default, ReceiverIdentity? identity = null)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || port is < 0 or > 65535) throw new ArgumentException("请填写接收电脑的具体 IP 和有效端口。");
        var receiver = new TransferReceiver(directory, progress, identity);
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(TransferReceiver).Assembly.FullName });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = Wire.ChunkSize + 16384;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
                options.Listen(ip, port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    listen.UseHttps(https => https.ServerCertificateSelector = (_, _) => receiver.certificates!.GetCertificate());
                });
            });
            var app = builder.Build();
            receiver.app = app;
            app.Run(receiver.HandleAsync);
            await app.StartAsync(ct);
            receiver.Invitation = new Invitation(app.Urls.Single().TrimEnd('/'), receiver.token,
                Wire.CertificatePin(receiver.certificates!.GetCertificate(), receiver.publicKeyPin), receiver.publicKeyPin);
            return receiver;
        }
        catch { await receiver.DisposeAsync(); throw; }
    }

    private string SafePath(string relative, bool allowParts = false)
    {
        if (!allowParts && !Wire.ValidName(relative)) throw new InvalidDataException("文件路径无效。");
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("文件路径超出接收目录。");
        RejectLinks(root);
        var current = root;
        foreach (var part in Path.GetRelativePath(root, result).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            RejectLinks(current);
        }
        return result;
    }
    private static void RejectLinks(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("接收路径不能使用符号链接或目录联接。"); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>Authenticate and dispatch POST /v2/begin, /v2/chunk and /v2/finish.</summary>
    /// <param name="context">HTTP context with bearer token, metadata or checksum-protected bytes.</param>
    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var receivedToken = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
        if (!CryptographicOperations.FixedTimeEquals(receivedToken, Encoding.UTF8.GetBytes("Bearer " + token)))
        { context.Response.StatusCode = 401; await context.Response.WriteAsync("连接码无效，请重新复制。"); return; }
        if (context.Request.Method != "POST") { context.Response.StatusCode = 405; return; }
        var ct = context.RequestAborted;
        try
        {
            if (context.Request.Path.StartsWithSegments("/v3/reverse"))
            {
                if (!await chunkSlots.WaitAsync(0, ct)) throw new TransferException(429, "接收端忙，请稍后重试。");
                try { await HandleReverseAsync(context, ct); }
                finally { chunkSlots.Release(); }
                return;
            }
            ResumeState result;
            if (context.Request.Path == "/v2/chunk")
            {
                if (!await chunkSlots.WaitAsync(0, ct)) throw new TransferException(429, "接收端忙，请稍后重试。");
                try { result = await ChunkAsync(context, ct); }
                finally { chunkSlots.Release(); }
            }
            else
            {
                await gate.WaitAsync(ct);
                try
                {
                    result = context.Request.Path.Value switch
                    {
                        "/v2/begin" => await BeginAsync(context, ct),
                        "/v2/finish" => await FinishAsync(context, ct),
                        _ => throw new TransferException(404, "接口不存在。")
                    };
                }
                finally { gate.Release(); }
            }
            await context.Response.WriteAsJsonAsync(result, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = error switch { TransferException e => e.Status, InvalidDataException => 400, JsonException => 400, _ => 500 };
                await context.Response.WriteAsync(error.Message, CancellationToken.None);
            }
            progress?.Report(new("", TransferStage.ReceiveError, Error: error.Message));
        }
    }

    // Caller must hold gate, as must BeginAsync, FinishAsync and Lookup.
    private void SignalProgress()
    {
        var previous = progressChanged;
        progressChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    /// <summary>Validate metadata and recover a persisted verified prefix.</summary>
    private async Task<ResumeState> BeginAsync(HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength is null or > 16384) throw new TransferException(400, "文件信息过大或无效。");
        var m = await context.Request.ReadFromJsonAsync<FileManifest>(ct) ?? throw new InvalidDataException("文件信息无效。");
        var parallelChunks = Wire.DefaultParallelChunks;
        if (context.Request.Query.TryGetValue("parallelChunks", out var requested))
        {
            if (!int.TryParse(requested, out var count) || count < 1) throw new InvalidDataException("并发数无效。");
            parallelChunks = Math.Min(count, Wire.MaxParallelChunks);
        }
        return await BeginFileAsync(m, parallelChunks, context.Request.Query["reset"] == "1", ct);
    }

    // Caller holds gate for begin/finish in both direct uploads and local reverse downloads.
    private async Task<ResumeState> BeginFileAsync(FileManifest m, int parallelChunks, bool reset, CancellationToken ct)
    {
        if (!Wire.ValidName(m.Name) || !Wire.ValidHash(m.Hash) || m.Size < 0) throw new InvalidDataException("文件信息无效。");
        var final = SafePath(m.Name);
        if (File.Exists(final))
        {
            if (new FileInfo(final).Length != m.Size || await Wire.FileHashAsync(final, ct) != m.Hash)
                throw new TransferException(409, "已有同名不同内容文件，已保留原文件：" + m.Name);
            progress?.Report(new(m.Name, TransferStage.ReceiveComplete, m.Size, m.Size, ParallelChunks: parallelChunks));
            return new(m.Size, "", true, parallelChunks);
        }
        var partial = SafePath($"{Wire.PartsDirectory}/{m.Id}.part", true);
        await using var file = new FileStream(partial, reset ? FileMode.Create : FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > m.Size) throw new TransferException(409, "临时文件大小异常，请更换接收目录。");
        var offset = file.Length;
        progress?.Report(new(m.Name, offset > 0 ? TransferStage.VerifyingResume : TransferStage.WaitingForData, offset, m.Size, ParallelChunks: parallelChunks));
        var prefix = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        if (sessions.TryGetValue(m.Id, out var previous)) previous.Stop();
        sessions[m.Id] = new(m, parallelChunks, offset);
        SignalProgress();
        return new(offset, prefix, ParallelChunks: parallelChunks);
    }
    private (ReceiveSession Session, string Partial) Lookup(string id)
    {
        if (!sessions.TryGetValue(id, out var session))
            throw new TransferException(409, "会话已重启，请重新连接。");
        return (session, SafePath($"{Wire.PartsDirectory}/{session.Manifest.Id}.part", true));
    }

    /// <summary>Accept one complete checksum-protected block and acknowledge only after disk flush.</summary>
    private async Task<ResumeState> ChunkAsync(HttpContext context, CancellationToken ct)
    {
        ReceiveSession session;
        string partial;
        await gate.WaitAsync(ct);
        try { (session, partial) = Lookup(context.Request.Query["id"].ToString()); }
        finally { gate.Release(); }
        var m = session.Manifest;
        if (!long.TryParse(context.Request.Query["offset"], out var offset) || offset < 0 || offset > m.Size)
            throw new InvalidDataException("文件偏移无效。");
        if (context.Request.ContentLength is not long length || length <= 0 || length > Wire.ChunkSize || length > m.Size - offset)
            throw new InvalidDataException("数据块大小无效。");
        session.Start();
        var buffer = new byte[(int)length];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await context.Request.Body.ReadAsync(buffer.AsMemory(read), ct);
            if (count == 0) throw new EndOfStreamException();
            read += count;
            session.Report(progress, bytes: count);
        }
        return await CommitChunkAsync(session, partial, offset, buffer, context.Request.Headers["X-Chunk-SHA256"].ToString(), ct);
    }

    private async Task<ResumeState> CommitChunkAsync(ReceiveSession session, string partial, long offset, byte[] buffer,
        string checksum, CancellationToken ct)
    {
        var m = session.Manifest;
        var length = buffer.Length;
        if (offset < 0 || offset > m.Size || length <= 0 || length > Wire.ChunkSize || length > m.Size - offset)
            throw new InvalidDataException("数据块大小无效。");
        if (Wire.Hash(buffer) != checksum)
            throw new InvalidDataException("数据块校验失败，未写入。");
        while (true)
        {
            Task changed;
            await gate.WaitAsync(ct);
            try
            {
                if (!sessions.TryGetValue(m.Id, out var current) || !ReferenceEquals(current, session))
                    throw new TransferException(409, "会话已变化，请重新连接。");
                var committed = new FileInfo(partial).Length;
                if (offset < committed) throw new TransferException(409, "进度已变化，请重新连接。");
                if (offset - committed > (session.ParallelChunks - 1L) * Wire.ChunkSize)
                    throw new InvalidDataException("数据块超出并行窗口。");
                if (offset == committed)
                {
                    await using var file = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None, Wire.ChunkSize, true);
                    if (file.Length != offset) throw new TransferException(409, "进度已变化，请重新连接。");
                    file.Position = offset;
                    await file.WriteAsync(buffer, ct);
                    file.Flush(flushToDisk: true);
                    SignalProgress();
                    session.Report(progress, committedOffset: offset + length);
                    return new(offset + length, "");
                }
                // Only acknowledge a contiguous, flushed prefix, even when later chunks arrive first.
                changed = progressChanged.Task;
            }
            finally { gate.Release(); }
            try { await changed.WaitAsync(TimeSpan.FromMinutes(5), ct); }
            catch (TimeoutException) { throw new TransferException(408, "前序数据块等待超时，请重新连接。"); }
        }
    }

    /// <summary>Verify whole-file checksum and atomically move into place without replacing a file.</summary>
    private Task<ResumeState> FinishAsync(HttpContext context, CancellationToken ct) =>
        FinishFileAsync(context.Request.Query["id"].ToString(), ct);

    private async Task<ResumeState> FinishFileAsync(string id, CancellationToken ct)
    {
        var (session, partial) = Lookup(id);
        var m = session.Manifest;
        progress?.Report(new(m.Name, TransferStage.VerifyingFile, m.Size, m.Size, ParallelChunks: session.ParallelChunks));
        if (new FileInfo(partial).Length != m.Size || await Wire.FileHashAsync(partial, ct) != m.Hash)
            throw new InvalidDataException("完整文件校验失败，未发布文件。");
        var final = SafePath(m.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        final = SafePath(m.Name);
        try { File.Move(partial, final, overwrite: false); }
        catch (IOException error) when (File.Exists(final)) { throw new TransferException(409, "同名文件已存在，保留原文件。", error); }
        sessions.Remove(m.Id);
        session.Stop();
        SignalProgress();
        progress?.Report(new(m.Name, TransferStage.ReceiveComplete, m.Size, m.Size, ParallelChunks: session.ParallelChunks));
        return new(m.Size, "", true);
    }
    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref outgoing, null)?.Completion.TrySetException(new IOException("反向发送服务已停止，请重新开始。"));
        var current = Interlocked.Exchange(ref app, null);
        if (current is not null)
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await current.StopAsync(shutdown.Token); }
            finally { await current.DisposeAsync(); certificates?.Dispose(); }
        }
        else certificates?.Dispose();
    }
}


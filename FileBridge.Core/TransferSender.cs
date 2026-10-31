using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileBridge.Core;

/// <summary>Bounded parallel chunk sender with ordered receiver commits, resume, and retry.</summary>
public static class TransferSender
{
    private const int MaxAttempts = 10;

    public static IReadOnlyList<TransferFile> Collect(IEnumerable<string> paths)
    {
        var result = new List<TransferFile>();
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var selected in paths)
        {
            var full = Path.GetFullPath(selected);
            var basePath = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar))!;
            Add(full);
            void Add(string file)
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不发送符号链接或目录联接：" + file);
                if ((attributes & FileAttributes.Directory) != 0)
                { foreach (var child in Directory.EnumerateFileSystemEntries(file)) Add(child); return; }
                var name = Path.GetRelativePath(basePath, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!Wire.ValidName(name) || !names.Add(name)) throw new InvalidDataException("文件名重复或无效：" + name);
                result.Add(new(file, name, new FileInfo(file).Length));
            }
        }
        if (result.Count == 0) throw new InvalidDataException("没有找到可发送的文件，空目录不会传输。");
        return result.OrderBy(f => f.RelativeName, StringComparer.Ordinal).ToList();
    }

    public static HttpClient CreateClient(Invitation invite)
    {
        var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
            MaxConnectionsPerServer = Wire.MaxParallelChunks, SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Wire.Hash(certificate.RawData)), Encoding.ASCII.GetBytes(invite.Pin))
            && DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime();
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5), BaseAddress = new Uri(invite.Url.TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.Authorization = new("Bearer", invite.Token);
        return client;
    }
    /// <summary>Send files using at most the selected number of parallel chunk requests.</summary>
    /// <param name="files">Ordered source files.</param>
    /// <param name="code">Receiver's authenticated invitation.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="ct">Cancellation retains the committed prefix for resuming.</param>
    /// <param name="parallelChunks">Sender limit, from 1 (single stream) to 10, default 4; the receiver may negotiate fewer.</param>
    public static async Task SendAsync(IReadOnlyList<TransferFile> files, string code, IProgress<TransferProgress>? progress, CancellationToken ct,
        int parallelChunks = Wire.DefaultParallelChunks)
    {
        if (parallelChunks is < 1 or > Wire.MaxParallelChunks) throw new ArgumentOutOfRangeException(nameof(parallelChunks));
        var invite = Invitation.Parse(code);
        using var client = CreateClient(invite);
        foreach (var source in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(source.RelativeName, TransferStage.CalculatingHash, 0, source.Size, ParallelChunks: 0));
            var before = new FileInfo(source.LocalPath);
            var modified = before.LastWriteTimeUtc;
            var size = before.Length;
            var hash = await Wire.FileHashAsync(source.LocalPath, ct);
            var m = new FileManifest(source.RelativeName, size, hash);
            for (var attempt = 0; ; attempt++)
            {
                var current = new FileInfo(source.LocalPath);
                if (current.Length != size || current.LastWriteTimeUtc != modified) throw new IOException("源文件发生变化，请停止编辑后重新发送。");
                try { await UploadAsync(client, source.LocalPath, m, progress, ct, StreamsForSize(size, parallelChunks)); break; }
                catch (Exception error) when (attempt < MaxAttempts - 1 && Retryable(error, ct))
                {
                    progress?.Report(new(m.Name, TransferStage.Retrying, 0, m.Size, ParallelChunks: 0,
                        RetryAttempt: attempt + 1, RetryLimit: MaxAttempts));
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << Math.Min(attempt, 4), 15)), ct);
                }
            }
        }
    }
    private static bool Retryable(Exception error, CancellationToken ct) => !ct.IsCancellationRequested && error switch
    {
        TransferException e => e.Status >= 500 || e.Status is 408 or 429 || e.Status == 409 && e.Message.Contains("重新连接", StringComparison.Ordinal),
        HttpRequestException e when e.InnerException is AuthenticationException => false,
        HttpRequestException => true,
        TaskCanceledException => true,
        EndOfStreamException => true,
        _ => false
    };

    public static async Task<ResumeState> RequestAsync(HttpClient client, string endpoint, HttpContent? body, CancellationToken ct, string? chunkHash = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = body };
        if (chunkHash is not null) request.Headers.Add("X-Chunk-SHA256", chunkHash);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(ct);
            throw new TransferException((int)response.StatusCode, message[..Math.Min(message.Length, 4096)]);
        }
        return await response.Content.ReadFromJsonAsync<ResumeState>(ct) ?? throw new InvalidDataException("接收端未返回有效进度。");
    }
    private static async Task<ResumeState> BeginAsync(HttpClient client, FileManifest m, CancellationToken ct, int parallelChunks, bool reset = false)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(m));
        content.Headers.ContentType = new("application/json");
        return await RequestAsync(client, $"v2/begin?parallelChunks={parallelChunks}" + (reset ? "&reset=1" : ""), content, ct);
    }

    internal static int StreamsForSize(long size, int requested) => (int)Math.Min(requested,
        size <= 0 ? 1 : (size - 1) / Wire.ChunkSize + 1);

    internal static int NegotiatedStreams(int requested, int supported) => Math.Min(requested, Math.Clamp(supported, 1, Wire.MaxParallelChunks));

    private static async Task UploadAsync(HttpClient client, string source, FileManifest m, IProgress<TransferProgress>? progress, CancellationToken ct, int parallelChunks)
    {
        var state = await BeginAsync(client, m, ct, parallelChunks);
        if (state.Done) { progress?.Report(new(m.Name, TransferStage.AlreadyComplete, m.Size, m.Size, ParallelChunks: NegotiatedStreams(parallelChunks, state.ParallelChunks))); return; }
        if (state.Offset < 0 || state.Offset > m.Size || !Wire.ValidHash(state.Prefix)) throw new InvalidDataException("接收端进度无效。");
        await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, Wire.ChunkSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        progress?.Report(new(m.Name, TransferStage.VerifyingResume, state.Offset, m.Size, ParallelChunks: NegotiatedStreams(parallelChunks, state.ParallelChunks)));
        using var prefix = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Wire.ChunkSize];
        var remaining = state.Offset;
        while (remaining > 0)
        {
            var n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (n == 0) throw new EndOfStreamException("源文件被截断。");
            prefix.AppendData(buffer, 0, n); remaining -= n;
        }
        if (Convert.ToHexStringLower(prefix.GetHashAndReset()) != state.Prefix)
        {
            state = await BeginAsync(client, m, ct, parallelChunks, true);
            if (state.Offset != 0) throw new InvalidDataException("无法重置损坏的临时文件。");
            progress?.Report(new(m.Name, TransferStage.ResendingCorrupt, 0, m.Size, ParallelChunks: NegotiatedStreams(parallelChunks, state.ParallelChunks)));
        }
        file.Position = state.Offset;
        var offset = state.Offset;
        var initial = offset;
        var clock = Stopwatch.StartNew();
        var parallelism = NegotiatedStreams(parallelChunks, state.ParallelChunks);
        var confirmed = offset;
        var reportGate = new object();
        using var uploads = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task>(parallelism);
        try
        {
            while (offset < m.Size)
            {
                var bytes = new byte[(int)Math.Min(Wire.ChunkSize, m.Size - offset)];
                await file.ReadExactlyAsync(bytes, uploads.Token);
                pending.Add(SendChunkAsync(offset, bytes));
                offset += bytes.Length;
                if (pending.Count == parallelism)
                {
                    await Task.WhenAll(pending);
                    pending.Clear();
                }
            }
            await Task.WhenAll(pending);
        }
        catch (Exception error)
        {
            uploads.Cancel();
            try { await Task.WhenAll(pending); }
            catch (Exception uploadError) when (error is OperationCanceledException && !ct.IsCancellationRequested)
            { ExceptionDispatchInfo.Capture(uploadError).Throw(); }
            catch { }
            throw;
        }

        async Task SendChunkAsync(long chunkOffset, byte[] bytes)
        {
            try
            {
                var acknowledgement = await RequestAsync(client, $"v2/chunk?id={m.Id}&offset={chunkOffset}",
                    new ByteArrayContent(bytes), uploads.Token, Wire.Hash(bytes));
                if (acknowledgement.Offset != chunkOffset + bytes.Length) throw new InvalidDataException("接收端确认进度不一致。");
                lock (reportGate)
                {
                    confirmed = Math.Max(confirmed, acknowledgement.Offset);
                    progress?.Report(new(m.Name, TransferStage.Sending, confirmed, m.Size,
                        (confirmed - initial) / Math.Max(clock.Elapsed.TotalSeconds, .001), parallelism));
                }
            }
            catch { uploads.Cancel(); throw; }
        }

        progress?.Report(new(m.Name, TransferStage.VerifyingFile, m.Size, m.Size, ParallelChunks: parallelism));
        await RequestAsync(client, "v2/finish?id=" + m.Id, null, ct);
        progress?.Report(new(m.Name, TransferStage.SendComplete, m.Size, m.Size, ParallelChunks: parallelism));
    }
}

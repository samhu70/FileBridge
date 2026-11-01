using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FileBridge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FileBridge.Tests;

internal static class ReverseChecks
{
    public static async Task RunAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        await SlowReadChecksAsync(ct);
        await TenConcurrentRequestsAsync(root, ct);
        var directory = Path.Combine(root, "reverse");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "source.bin");
        var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize * 3 + 97);
        await File.WriteAllBytesAsync(source, bytes, ct);
        var empty = Path.Combine(directory, "empty");
        await File.WriteAllBytesAsync(empty, [], ct);
        TransferFile[] files = [new(source, "中文/large.bin", bytes.Length), new(empty, "empty.bin", 0)];
        var manifest = new FileManifest(files[0].RelativeName, bytes.Length, Wire.Hash(bytes));
        var identity = ReceiverIdentity.Create();
        var hostDirectory = Path.Combine(directory, "host-received");
        var host = await TransferReceiver.StartAsync("127.0.0.1", 0, hostDirectory, identity: identity);
        try
        {
            var code = host.Invitation.Encode();
            using var client = TransferSender.CreateClient(host.Invitation);
            using var offerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var sentEvents = new List<TransferProgress>();
            var sentGate = new object();
            var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var offering = host.SendReverseAsync(files,
                new InlineProgress(p =>
                {
                    lock (sentGate) sentEvents.Add(p);
                    if (p.Stage == TransferStage.WaitingForPeer && p.FileName.Length == 0) prepared.TrySetResult();
                }), offerCancellation.Token, 10);
            await prepared.Task.WaitAsync(ct);
            lock (sentGate) Check(files.All(file => sentEvents.Any(p => p.FileName == file.RelativeName && p.Stage == TransferStage.WaitingForPeer)),
                "reverse offer ready state did not replace per-file hashing state");
            var offer = await WaitForOfferAsync(client, ct);
            Check(offer.ChunkSize == Wire.ReverseChunkSize, "new reverse offers did not advertise smaller blocks");
            Check(offer.Files.Length == 2 && offer.Files.All(f => f.Name is "中文/large.bin" or "empty.bin"), "unselected file was offered");
            using (var legacyChunk = await client.PostAsync($"v3/reverse/chunk?offer={offer.Id}&id={manifest.Id}&offset=0&parallelChunks=2", null, ct))
            {
                Check(legacyChunk.IsSuccessStatusCode && legacyChunk.Content.Headers.ContentLength == Wire.ChunkSize,
                    "an old receiver without a length parameter no longer receives four-MiB chunks");
                Check((await legacyChunk.Content.ReadAsByteArrayAsync(ct)).SequenceEqual(bytes[..Wire.ChunkSize]),
                    "legacy reverse chunk contents changed");
            }
            using (var badLength = await client.PostAsync($"v3/reverse/chunk?offer={offer.Id}&id={manifest.Id}&offset=0&length=0", null, ct))
                Check(badLength.StatusCode == HttpStatusCode.BadRequest, "reverse chunk accepted invalid length");
            using (var wrong = TransferSender.CreateClient(host.Invitation with { Token = new string('0', 64) }))
            using (var denied = await wrong.PostAsync("v3/reverse/list", null, ct))
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "reverse listing bypassed authentication");
            using (var denied = await client.PostAsync($"v3/reverse/chunk?offer={offer.Id}&id=../../secret&offset=0", null, ct))
                Check(denied.StatusCode == HttpStatusCode.Conflict, "reverse path injection was accepted");
            await MustFail(() => ReverseTransfer.ReceiveAsync((host.Invitation with { Pin = new string('0', 64) }).Encode(),
                Path.Combine(directory, "wrong-pin"), null, ct), "reverse download ignored TLS pin");

            // The only listener is host. Both upload and download clients connect to that same endpoint.
            var destination = Path.Combine(directory, "peer-received");
            var direct = TransferSender.SendAsync([new(empty, "peer-to-host.bin", 0)], code, null, ct);
            await Task.WhenAll(direct, ReverseTransfer.ReceiveAsync(code, destination, null, ct, 2)).WaitAsync(ct);
            await offering.WaitAsync(ct);
            Check(File.Exists(Path.Combine(hostDirectory, "peer-to-host.bin")), "normal upload broke during reverse download");
            Check(await Wire.FileHashAsync(Path.Combine(destination, manifest.Name), ct) == manifest.Hash, "reverse SHA-256 mismatch");
            Check(new FileInfo(Path.Combine(destination, "empty.bin")).Length == 0, "reverse empty file failed");
            lock (sentGate) Check(sentEvents.Where(p => p.FileName == manifest.Name && p.Stage is TransferStage.Sending or TransferStage.SendComplete)
                .All(p => p.ParallelChunks == 2), "reverse source progress ignored negotiated download streams");
            using (var withdrawn = await client.PostAsync("v3/reverse/list", null, ct))
                Check(withdrawn.StatusCode == HttpStatusCode.Conflict, "completed offer still exposed source files");
            using (var receipt = await client.PostAsync($"v3/reverse/complete?offer={offer.Id}&id={manifest.Id}", null, ct))
                Check(receipt.IsSuccessStatusCode, "completion receipt retry failed");
            Console.WriteLine("PASS reverse real HTTPS / bidirectional single endpoint / Unicode and empty files / TLS and token / selected files only / idempotent completion");

            var tenStreamSource = Path.Combine(directory, "ten-streams.bin");
            await using (var file = File.Create(tenStreamSource)) file.SetLength(Wire.ChunkSize * 10L);
            var tenStreamEvents = new List<TransferProgress>();
            offering = host.SendReverseAsync([new(tenStreamSource, "ten-streams.bin", Wire.ChunkSize * 10L)], null, ct, 10);
            await WaitForOfferAsync(client, ct);
            var tenStreamDestination = Path.Combine(directory, "ten-streams-received");
            await ReverseTransfer.ReceiveAsync(code, tenStreamDestination,
                new InlineProgress(p =>
                {
                    tenStreamEvents.Add(p);
                    Check(p.ParallelChunks == 10, "default reverse receive still caps sender-selected ten streams");
                }), ct);
            await offering.WaitAsync(ct);
            Check(tenStreamEvents.Any(p => p.Stage == TransferStage.ReceiveComplete)
                && await Wire.FileHashAsync(Path.Combine(tenStreamDestination, "ten-streams.bin"), ct)
                    == await Wire.FileHashAsync(tenStreamSource, ct), "ten-stream reverse transfer did not complete with checksum");
            Console.WriteLine("PASS reverse default follows sender-selected ten streams / explicit lower receiver cap / final checksum");

            var resumeDirectory = Path.Combine(directory, "resumed");
            using var pausedOffer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            offering = host.SendReverseAsync([files[0]], null, pausedOffer.Token, 1);
            await WaitForOfferAsync(client, ct);
            using (var pause = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                await MustFail(() => ReverseTransfer.ReceiveAsync(code, resumeDirectory,
                    new InlineProgress(p => { if (p.Stage == TransferStage.Receiving && p.Completed >= Wire.ChunkSize) pause.Cancel(); }),
                    pause.Token, 1), "reverse pause was ignored");
            }
            var partial = Path.Combine(resumeDirectory, Wire.PartsDirectory, manifest.Id + ".part");
            Check(new FileInfo(partial).Length == Wire.ChunkSize && !File.Exists(Path.Combine(resumeDirectory, manifest.Name)),
                "reverse pause lost durable prefix or published an incomplete file");
            pausedOffer.Cancel();
            await MustFail(() => offering, "source pause did not withdraw offer");
            var port = new Uri(host.Invitation.Url).Port;
            await host.DisposeAsync();
            host = await TransferReceiver.StartAsync("127.0.0.1", port, hostDirectory, identity: identity);
            Check(host.Invitation.Encode() == code, "reverse host restart changed connection identity");
            offering = host.SendReverseAsync([files[0]], null, ct, 1);
            using var restartedClient = TransferSender.CreateClient(host.Invitation);
            await WaitForOfferAsync(restartedClient, ct);
            var resumed = false;
            await ReverseTransfer.ReceiveAsync(code, resumeDirectory,
                new InlineProgress(p => { if (p.Stage == TransferStage.VerifyingResume && p.Completed == Wire.ChunkSize) resumed = true; }), ct, 1);
            await offering.WaitAsync(ct);
            Check(resumed && await Wire.FileHashAsync(Path.Combine(resumeDirectory, manifest.Name), ct) == manifest.Hash,
                "reverse restart did not resume and verify persisted prefix");
            Console.WriteLine("PASS reverse pause / durable prefix / host restart with same code / verified resume");

            var corruptDirectory = Path.Combine(directory, "corrupt-prefix");
            Directory.CreateDirectory(Path.Combine(corruptDirectory, Wire.PartsDirectory));
            await File.WriteAllBytesAsync(Path.Combine(corruptDirectory, Wire.PartsDirectory, manifest.Id + ".part"), new byte[Wire.ChunkSize], ct);
            offering = host.SendReverseAsync([files[0]], null, ct);
            await WaitForOfferAsync(restartedClient, ct);
            var reset = false;
            await ReverseTransfer.ReceiveAsync(code, corruptDirectory,
                new InlineProgress(p => { if (p.Stage == TransferStage.ResendingCorrupt) reset = true; }), ct);
            await offering.WaitAsync(ct);
            Check(reset && await Wire.FileHashAsync(Path.Combine(corruptDirectory, manifest.Name), ct) == manifest.Hash,
                "reverse corrupt prefix was not reset and repaired");
            Console.WriteLine("PASS reverse corrupt-prefix recovery / parallel ordered commits / final checksum");

            await AutoResumeAsync(directory, host, restartedClient, files[0], manifest, ct);

            // An existing different final file is never overwritten, even by an authenticated offering peer.
            var conflict = Path.Combine(directory, "conflict");
            Directory.CreateDirectory(Path.Combine(conflict, "中文"));
            var existing = Path.Combine(conflict, manifest.Name);
            await File.WriteAllTextAsync(existing, "keep me", ct);
            using (var stopped = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                offering = host.SendReverseAsync([files[0]], null, stopped.Token);
                await WaitForOfferAsync(restartedClient, ct);
                await MustFail(() => ReverseTransfer.ReceiveAsync(code, conflict, null, ct), "reverse transfer overwrote a different file");
                Check(await File.ReadAllTextAsync(existing, ct) == "keep me", "conflicting file changed");
                if (!OperatingSystem.IsWindows())
                {
                    var link = Path.Combine(directory, "linked-destination");
                    Directory.CreateSymbolicLink(link, conflict);
                    await MustFail(() => ReverseTransfer.ReceiveAsync(code, link, null, ct), "reverse destination accepted a symlink");
                }
                stopped.Cancel();
                await MustFail(() => offering, "cancelled source task stayed active");
            }
            await using (var local = TransferReceiver.CreateLocal(Path.Combine(directory, "checksum-store"), null))
            {
                await local.BeginLocalAsync(manifest, 1, false, ct);
                await MustFail(() => local.WriteLocalAsync(manifest.Id, 0, bytes[..Wire.ChunkSize], new string('0', 64), ct),
                    "local reverse receive accepted a corrupt chunk");
                Check(new FileInfo(Path.Combine(directory, "checksum-store", Wire.PartsDirectory, manifest.Id + ".part")).Length == 0,
                    "corrupt chunk changed the verified prefix");
                await MustFail(() => local.FinishLocalAsync(manifest.Id, ct), "incomplete reverse file was published");
            }
            Console.WriteLine("PASS reverse no-overwrite / symlink rejection / chunk integrity before disk commit / incomplete final rejection");
            offering = host.SendReverseAsync([files[0]], null, ct);
            var changing = await WaitForOfferAsync(restartedClient, ct);
            await File.WriteAllTextAsync(source, "changed after offering", ct);
            using (var changed = await restartedClient.PostAsync($"v3/reverse/chunk?offer={changing.Id}&id={manifest.Id}&offset=0", null, ct))
                Check(changed.StatusCode == HttpStatusCode.InternalServerError, "changed source was served under an old manifest");
            await MustFail(() => offering, "changed source left the sender waiting forever");
            Console.WriteLine("PASS reverse source mutation rejects download and releases sender");
            await MaliciousPeerAsync(directory, ct);
        }
        finally { await host.DisposeAsync(); }
    }

    private static async Task AutoResumeAsync(string directory, TransferReceiver host, HttpClient client,
        TransferFile source, FileManifest manifest, CancellationToken ct)
    {
        foreach (var mode in new[] { "resume", "changed", "cancel" })
        {
            using var sourcePause = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var receiverPause = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var offering = host.SendReverseAsync([source], null, sourcePause.Token, 1);
            var oldOffer = await WaitForOfferAsync(client, ct);
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumed = false;
            var paused = false;
            var destination = Path.Combine(directory, "auto-" + mode);
            var receiving = ReverseTransfer.ReceiveAsync(host.Invitation.Encode(), destination, new InlineProgress(p =>
            {
                if (p.Stage == TransferStage.Receiving && p.Completed >= Wire.ReverseChunkSize && !paused)
                { paused = true; sourcePause.Cancel(); }
                if (p.Stage == TransferStage.WaitingForSender && paused)
                {
                    Check(p.Completed >= Wire.ReverseChunkSize && p.BytesPerSecond == 0, "waiting lost durable progress or retained speed");
                    waiting.TrySetResult();
                }
                if (p.Stage == TransferStage.VerifyingResume && p.Completed >= Wire.ReverseChunkSize && p.ParallelChunks == 3)
                    resumed = true;
            }), receiverPause.Token);
            await MustFail(() => offering, "source pause failed to withdraw its offer");
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Check(!receiving.IsCompleted, "sender pause ended the receiver instead of waiting");
            var partial = Path.Combine(destination, Wire.PartsDirectory, manifest.Id + ".part");
            var prefix = new FileInfo(partial).Length;
            if (mode == "cancel")
            {
                receiverPause.Cancel();
                var cancelled = false;
                try { await receiving; }
                catch (OperationCanceledException) when (receiverPause.IsCancellationRequested) { cancelled = true; }
                Check(cancelled && new FileInfo(partial).Length == prefix, "receiver pause did not cancel automatic waiting safely");
                continue;
            }
            using var newPause = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var nextSource = source;
            if (mode == "changed")
            {
                var changed = Path.Combine(directory, "changed-source.bin");
                await File.WriteAllTextAsync(changed, "different contents with the same offered name", ct);
                nextSource = new(changed, source.RelativeName, new FileInfo(changed).Length);
            }
            offering = host.SendReverseAsync([nextSource], null, newPause.Token, 3);
            var nextOffer = await WaitForOfferAsync(client, ct);
            Check(nextOffer.Id != oldOffer.Id, "test did not replace the offer identity");
            if (mode == "changed")
            {
                var rejected = false;
                try { await receiving; }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && new FileInfo(partial).Length == prefix && !File.Exists(Path.Combine(destination, manifest.Name)),
                    "automatic reconnect accepted a substituted manifest or changed its prefix");
                newPause.Cancel();
                await MustFail(() => offering, "changed offer stayed active after cancellation");
            }
            else
            {
                await Task.WhenAll(receiving, offering).WaitAsync(ct);
                Check(resumed && await Wire.FileHashAsync(Path.Combine(destination, manifest.Name), ct) == manifest.Hash,
                    "receiver did not automatically resume the original manifest with checksum after reoffer");
            }
        }
        using var idlePause = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var idleWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idle = ReverseTransfer.ReceiveAsync(host.Invitation.Encode(), Path.Combine(directory, "initial-wait"),
            new InlineProgress(p => { if (p.Stage == TransferStage.WaitingForSender) idleWaiting.TrySetResult(); }), idlePause.Token);
        await idleWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var initialOffer = host.SendReverseAsync([source], null, ct, 1);
        await Task.WhenAll(idle, initialOffer).WaitAsync(ct);
        Console.WriteLine("PASS reverse automatic wait/reoffer resume / durable prefix and renegotiation / receiver pause / changed manifest rejection / connect before sender ready");
    }

    private static async Task<ReverseOffer> WaitForOfferAsync(HttpClient client, CancellationToken ct)
    {
        for (var i = 0; i < 500; i++)
        {
            using var response = await client.PostAsync("v3/reverse/list", null, ct);
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<ReverseOffer>(ct))!;
            await Task.Delay(10, ct);
        }
        throw new TimeoutException("Reverse offer not ready");
    }

    private static async Task MaliciousPeerAsync(string directory, CancellationToken ct)
    {
        var identity = ReceiverIdentity.Create();
        using var certificate = identity.LoadCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        await using var server = builder.Build();
        var offerId = new string('a', 64);
        var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize + 17);
        var manifest = new FileManifest("small.bin", bytes.Length, Wire.Hash(bytes));
        var mode = "path";
        var failedOnce = false;
        var receiptFailed = false;
        server.Run(async context =>
        {
            if (context.Request.Path == "/v3/reverse/list")
            {
                if (mode == "old") { context.Response.StatusCode = 404; return; }
                if (mode == "retry" && receiptFailed) { context.Response.StatusCode = 409; return; }
                if (mode == "disconnect-list" && !failedOnce)
                {
                    failedOnce = true;
                    context.Response.ContentLength = 1000;
                    await context.Response.Body.WriteAsync(new byte[] { (byte)'{' }, ct);
                    await context.Response.Body.FlushAsync(ct);
                    context.Abort();
                    return;
                }
                await context.Response.WriteAsJsonAsync(new ReverseOffer(offerId,
                    [mode == "path" ? manifest with { Name = "../escape.bin" } : manifest], 1), ct);
            }
            else if (context.Request.Path == "/v3/reverse/chunk")
            {
                var offset = int.Parse(context.Request.Query["offset"]!);
                if (mode == "retry" && offset > 0 && !failedOnce)
                { failedOnce = true; context.Response.StatusCode = 503; return; }
                var chunk = bytes.AsSpan(offset, Math.Min(Wire.ChunkSize, bytes.Length - offset)).ToArray();
                if (mode == "whole") chunk[0] ^= 0xff;
                context.Response.ContentLength = chunk.Length;
                context.Response.Headers["X-Chunk-SHA256"] = mode == "chunk" ? new string('0', 64) : Wire.Hash(chunk);
                if (mode == "disconnect" && !failedOnce)
                {
                    failedOnce = true;
                    await context.Response.Body.WriteAsync(chunk.AsMemory(0, 65536), ct);
                    await context.Response.Body.FlushAsync(ct);
                    context.Abort();
                    return;
                }
                if (mode == "slow")
                {
                    for (var position = 0; position < chunk.Length; position += 65536)
                    {
                        await context.Response.Body.WriteAsync(chunk.AsMemory(position, Math.Min(65536, chunk.Length - position)), ct);
                        await Task.Delay(20, ct);
                    }
                }
                else await context.Response.Body.WriteAsync(chunk, ct);
            }
            else if (context.Request.Path == "/v3/reverse/prefix")
            {
                var length = int.Parse(context.Request.Query["length"]!);
                await context.Response.WriteAsJsonAsync(new ResumeState(length, Wire.Hash(bytes.AsSpan(0, length))), ct);
            }
            else if (context.Request.Path == "/v3/reverse/complete")
            {
                if (mode == "retry" && !receiptFailed)
                { receiptFailed = true; context.Response.StatusCode = 503; return; }
                await context.Response.WriteAsJsonAsync(new ResumeState(bytes.Length, "", true), ct);
            }
        });
        await server.StartAsync(ct);
        var invite = identity.InvitationFor("127.0.0.1", new Uri(server.Urls.Single()).Port);
        foreach (var test in new[] { "path", "old", "chunk", "whole" })
        {
            mode = test;
            var destination = Path.Combine(directory, "malicious-" + test);
            await MustFail(() => ReverseTransfer.ReceiveAsync(invite.Encode(), destination, null, ct), "malicious reverse peer was accepted: " + test);
            Check(!File.Exists(Path.Combine(destination, "small.bin")) && !File.Exists(Path.Combine(directory, "escape.bin")),
                "malicious peer published data: " + test);
        }
        Console.WriteLine("PASS reverse malicious metadata / unsupported peer / corrupt network chunk / untrusted checksum");
        mode = "retry";
        var events = new List<TransferProgress>();
        var resumed = Path.Combine(directory, "retry-received");
        await ReverseTransfer.ReceiveAsync(invite.Encode(), resumed, new InlineProgress(events.Add), ct, 1);
        Check(events.Any(p => p.Stage == TransferStage.Retrying)
            && events.Any(p => p.Stage == TransferStage.VerifyingResume && p.Completed == Wire.ChunkSize)
            && await Wire.FileHashAsync(Path.Combine(resumed, manifest.Name), ct) == manifest.Hash,
            "reverse transient network failure did not retry from the verified prefix");
        Check(receiptFailed, "completion receipt retry was not exercised");
        Console.WriteLine("PASS reverse transient HTTP failure / automatic retry / verified prefix reuse / final receipt retry after offer withdrawal");
        mode = "disconnect";
        failedOnce = false;
        var disconnectedDestination = Path.Combine(directory, "disconnected-received");
        await ReverseTransfer.ReceiveAsync(invite.Encode(), disconnectedDestination, null, ct);
        Check(failedOnce && await Wire.FileHashAsync(Path.Combine(disconnectedDestination, manifest.Name), ct) == manifest.Hash,
            "an interrupted response body did not automatically reconnect and verify the file");
        Console.WriteLine("PASS reverse interrupted response body / automatic reconnect / final checksum");
        mode = "disconnect-list";
        failedOnce = false;
        var disconnectedList = Path.Combine(directory, "disconnected-list-received");
        await ReverseTransfer.ReceiveAsync(invite.Encode(), disconnectedList, null, ct);
        Check(failedOnce && await Wire.FileHashAsync(Path.Combine(disconnectedList, manifest.Name), ct) == manifest.Hash,
            "an interrupted offer response did not reconnect and verify the file");
        Console.WriteLine("PASS reverse interrupted offer response / initial reconnect / final checksum");
        mode = "slow";
        var reading = new List<TransferProgress>();
        var slowDestination = Path.Combine(directory, "slow-received");
        await ReverseTransfer.ReceiveAsync(invite.Encode(), slowDestination, new InlineProgress(reading.Add), ct, 1);
        Check(reading.Any(p => p.Stage == TransferStage.Receiving && p.Completed == 0 && p.BytesPerSecond > 0)
            && await Wire.FileHashAsync(Path.Combine(slowDestination, manifest.Name), ct) == manifest.Hash,
            "slow network reads were hidden until a whole chunk was committed");
        Console.WriteLine("PASS reverse slow HTTPS / in-flight speed before first verified chunk / final checksum");
    }

    private static async Task TenConcurrentRequestsAsync(string directory, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        ct = deadline.Token;
        var identity = ReceiverIdentity.Create();
        using var certificate = identity.LoadCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        await using var server = builder.Build();
        var bytes = new byte[Wire.ChunkSize * 10];
        var manifest = new FileManifest("ten-concurrent.bin", bytes.Length, Wire.Hash(bytes));
        var offerId = new string('b', 64);
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        server.Run(async context =>
        {
            if (context.Request.Path == "/v3/reverse/list")
                await context.Response.WriteAsJsonAsync(new ReverseOffer(offerId, [manifest], 10, Wire.ReverseChunkSize), ct);
            else if (context.Request.Path == "/v3/reverse/chunk")
            {
                Check(context.Request.Query["parallelChunks"] == "10", "reverse chunk request did not negotiate ten streams");
                // No first-wave response can finish until all ten requests reach the server together.
                if (Interlocked.Increment(ref started) == 10) firstWave.TrySetResult();
                await firstWave.Task.WaitAsync(ct);
                var offset = int.Parse(context.Request.Query["offset"]!);
                var length = int.Parse(context.Request.Query["length"]!);
                var chunk = bytes.AsMemory(offset, length);
                context.Response.ContentLength = length;
                context.Response.Headers["X-Chunk-SHA256"] = Wire.Hash(chunk.Span);
                await context.Response.Body.WriteAsync(chunk, ct);
            }
            else if (context.Request.Path == "/v3/reverse/complete")
                await context.Response.WriteAsJsonAsync(new ResumeState(bytes.Length, "", true, 10), ct);
            else context.Response.StatusCode = 404;
        });
        await server.StartAsync(ct);
        var invite = identity.InvitationFor("127.0.0.1", new Uri(server.Urls.Single()).Port);
        var destination = Path.Combine(directory, "ten-concurrent");
        await ReverseTransfer.ReceiveAsync(invite.Encode(), destination, null, ct);
        Check(firstWave.Task.IsCompletedSuccessfully && started == bytes.Length / Wire.ReverseChunkSize
            && await Wire.FileHashAsync(Path.Combine(destination, manifest.Name), ct) == manifest.Hash,
            "default reverse receiver did not complete ten simultaneous HTTPS requests with checksum");
        Console.WriteLine("PASS reverse ten simultaneous HTTPS requests / default receiver follows sender / final checksum");
    }

    private static async Task SlowReadChecksAsync(CancellationToken ct)
    {
        var expected = RandomNumberGenerator.GetBytes(8192);
        var actual = new byte[expected.Length];
        var count = 0;
        using (var slow = new DelayedReadStream(expected, TimeSpan.FromMilliseconds(80), 512))
            await ReverseTransfer.ReadChunkAsync(slow, actual, n => count += n, TimeSpan.FromMilliseconds(500), ct);
        Check(actual.SequenceEqual(expected) && count == expected.Length,
            "a progressing read longer than the idle timeout was aborted or miscounted");
        var idleTimedOut = false;
        using (var idle = new DelayedReadStream([1], TimeSpan.FromSeconds(1), 1))
        {
            try { await ReverseTransfer.ReadChunkAsync(idle, new byte[1], _ => { }, TimeSpan.FromMilliseconds(80), ct); }
            catch (TransferException error) when (error.Status == 408)
            { idleTimedOut = TransferSender.Retryable(error, ct); }
        }
        Check(idleTimedOut, "a stalled read did not produce a retryable inactivity timeout");
        using (var pause = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var slow = new DelayedReadStream([1], TimeSpan.FromSeconds(1), 1))
        {
            pause.CancelAfter(50);
            var paused = false;
            try { await ReverseTransfer.ReadChunkAsync(slow, new byte[1], _ => { }, TimeSpan.FromSeconds(2), pause.Token); }
            catch (OperationCanceledException) when (pause.IsCancellationRequested) { paused = true; }
            Check(paused, "user pause was converted into an inactivity failure");
        }
        using (var truncated = new MemoryStream([1]))
        {
            var ended = false;
            try { await ReverseTransfer.ReadChunkAsync(truncated, new byte[2], _ => { }, TimeSpan.FromSeconds(1), ct); }
            catch (EndOfStreamException) { ended = true; }
            Check(ended, "truncated chunk was treated as complete");
        }
        Console.WriteLine("PASS reverse sliding idle timeout / continuously progressing slow reads / true stalls retry / user pause / truncated body");
    }

    private sealed class DelayedReadStream(byte[] bytes, TimeSpan delay, int slice) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, slice)], cancellationToken);
        }
    }

    private static async Task MustFail(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is not TimeoutException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

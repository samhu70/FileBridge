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
            var offering = host.SendReverseAsync(files,
                new InlineProgress(p => { lock (sentGate) sentEvents.Add(p); }), offerCancellation.Token, 10);
            var offer = await WaitForOfferAsync(client, ct);
            Check(offer.Files.Length == 2 && offer.Files.All(f => f.Name is "中文/large.bin" or "empty.bin"), "unselected file was offered");
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
                await context.Response.Body.WriteAsync(chunk, ct);
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
    }

    private static async Task MustFail(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) when (error is not TimeoutException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

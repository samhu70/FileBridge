using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FileBridge.Core;
using FileBridge.Desktop;

namespace FileBridge.Tests;

internal static class DualEndpointChecks
{
    public static async Task RunAsync(string root)
    {
        ProtocolChecks.Run();
        FunnelConfiguration();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var destination = Path.Combine(root, "dual-upload");
        await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, destination, loopback: true);
        var target = new Uri(receiver.Invitation.Url).Port;
        Check(receiver.LoopbackPort == target, "loopback listener was not reused");
        using var direct = new TcpForwarder(target);
        using var fallback = new TcpForwarder(target);
        var invite = receiver.Invitation with { Url = direct.Url, PublicUrl = fallback.Url };
        using (var connection = new PeerConnection(invite))
        {
            Check((await connection.GetAsync(ct)).BaseAddress!.GetLeftPart(UriPartial.Authority) == direct.Url,
                "reachable direct endpoint was not preferred");
            Check(fallback.Accepted == 0, "public fallback was contacted when direct was healthy");
        }
        foreach (var bad in new[] { invite with { Token = new string('0', 64) }, invite with { Pin = new string('0', 64) } })
        {
            using var denied = new PeerConnection(bad);
            await MustFail(() => denied.GetAsync(ct), "dual endpoints bypassed token or certificate pin");
        }
        var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize * 3 + 73);
        var source = Path.Combine(root, "dual-source.bin");
        await File.WriteAllBytesAsync(source, bytes, ct);
        TransferFile file = new(source, "中文/data.bin", bytes.Length);
        var interrupted = false;
        var resumed = false;
        await TransferSender.SendAsync([file], invite.Encode(), new InlineProgress(p =>
        {
            if (!interrupted && p.Stage == TransferStage.Sending && p.Completed >= Wire.ChunkSize && p.Completed < p.Total)
            { interrupted = true; direct.Dispose(); }
            if (interrupted && p.Stage == TransferStage.VerifyingResume && p.Completed >= Wire.ChunkSize) resumed = true;
        }), ct, 1);
        Check(interrupted && resumed && fallback.Accepted > 0, "upload did not resume through fallback after direct failed");
        Check(await Wire.FileHashAsync(Path.Combine(destination, file.RelativeName), ct) == Wire.Hash(bytes), "fallback upload hash mismatch");
        Console.WriteLine("PASS dual direct preference / same token and pin / interrupted upload resumes through fallback / SHA-256");

        using var reverseDirect = new TcpForwarder(target);
        invite = invite with { Url = reverseDirect.Url };
        var reverseCut = false;
        var reverseResumed = false;
        var offering = receiver.SendReverseAsync([file], null, ct, 1);
        var reverseDestination = Path.Combine(root, "dual-reverse");
        await ReverseTransfer.ReceiveAsync(invite.Encode(), reverseDestination, new InlineProgress(p =>
        {
            if (!reverseCut && p.Stage == TransferStage.Receiving && p.Completed >= Wire.ChunkSize && p.Completed < p.Total)
            { reverseCut = true; reverseDirect.Dispose(); }
            if (reverseCut && p.Stage == TransferStage.VerifyingResume && p.Completed >= Wire.ChunkSize) reverseResumed = true;
        }), ct, 1);
        await offering.WaitAsync(ct);
        Check(reverseCut && reverseResumed && await Wire.FileHashAsync(Path.Combine(reverseDestination, file.RelativeName), ct) == Wire.Hash(bytes),
            "reverse download did not resume through fallback with verified prefix");
        Console.WriteLine("PASS dual reverse download interruption / fallback / durable prefix / SHA-256");

        using var stalled = new TcpForwarder(null);
        using var stalledConnection = new PeerConnection(invite with { Url = stalled.Url });
        var started = Environment.TickCount64;
        Check((await stalledConnection.GetAsync(ct)).BaseAddress!.GetLeftPart(UriPartial.Authority) == fallback.Url
            && Environment.TickCount64 - started < 8000, "stalled direct connection did not fall back promptly");
        var publicBefore = fallback.Accepted;
        using var cancelled = new PeerConnection(invite with { Url = stalled.Url });
        using var pause = new CancellationTokenSource(100);
        try { await cancelled.GetAsync(pause.Token); throw new Exception("endpoint selection ignored cancellation"); }
        catch (OperationCanceledException) when (pause.IsCancellationRequested) { }
        Check(fallback.Accepted == publicBefore, "cancelled connection still attempted public fallback");
        Console.WriteLine("PASS stalled endpoint timeout / fallback within eight seconds / user cancellation");
    }

    private static void FunnelConfiguration()
    {
        using var config = JsonDocument.Parse("""
        {"TCP":{"443":{"HTTPS":true}},"Web":{"host:443":{"Handlers":{"/":{"Proxy":"https://localhost:7063"}}}},
         "Foreground":{"one":{"TCP":{"8443":{"TCPForward":"127.0.0.1:9999"}},"AllowFunnel":{"test.tailnet.ts.net:8443":true}}}}
        """);
        Check(TailscaleFunnel.SelectPort(config.RootElement, 443) == 10000, "existing root or foreground service was overwritten");
        Check(TailscaleFunnel.HasForwarder(config.RootElement, 8443, 9999, "test.tailnet.ts.net"), "foreground raw TCP forwarder not recognized");
        using var terminated = JsonDocument.Parse("""
        {"TCP":{"8443":{"TCPForward":"127.0.0.1:9999","TerminateTLS":"test.tailnet.ts.net"}},"AllowFunnel":{"test.tailnet.ts.net:8443":true}}
        """);
        Check(!TailscaleFunnel.HasForwarder(terminated.RootElement, 8443, 9999, "test.tailnet.ts.net"), "TLS termination accepted despite pinned FileBridge TLS");
        using var full = JsonDocument.Parse("""{"TCP":{"443":{},"8443":{},"10000":{}}}""");
        try { TailscaleFunnel.SelectPort(full.RootElement); throw new Exception("occupied public port selected"); }
        catch (FunnelSetupException) { }
        using var empty = JsonDocument.Parse("{}");
        Check(TailscaleFunnel.SelectPort(empty.RootElement, 10000) == 10000, "saved public port was not preferred");
        Console.WriteLine("PASS Funnel port conflict avoidance / foreground sessions / no TLS termination / saved port reuse");
    }

    private static async Task MustFail(Func<Task> operation, string message)
    {
        try { await operation(); }
        catch (Exception error) when (error is HttpRequestException or TransferException) { return; }
        throw new Exception(message);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class InlineProgress(Action<TransferProgress> action) : IProgress<TransferProgress>
    { public void Report(TransferProgress value) => action(value); }

    // A real TCP relay lets the tests cut a route while keeping the receiver and its identity alive.
    private sealed class TcpForwarder : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> clients = [];
        private int disposed, accepted;
        public int Accepted => Volatile.Read(ref accepted);
        public string Url { get; }
        public TcpForwarder(int? target)
        {
            listener.Start();
            Url = $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
            _ = AcceptAsync(target);
        }
        private async Task AcceptAsync(int? target)
        {
            try
            {
                while (true)
                {
                    var incoming = await listener.AcceptTcpClientAsync(stopping.Token);
                    clients.Add(incoming);
                    Interlocked.Increment(ref accepted);
                    if (target is not null) _ = ForwardAsync(incoming, target.Value);
                }
            }
            catch (Exception error) when (stopping.IsCancellationRequested && error is OperationCanceledException or SocketException) { }
        }
        private async Task ForwardAsync(TcpClient incoming, int target)
        {
            using var outgoing = new TcpClient();
            clients.Add(outgoing);
            try
            {
                await outgoing.ConnectAsync(IPAddress.Loopback, target, stopping.Token);
                using var stream = incoming.GetStream();
                using var peer = outgoing.GetStream();
                var upload = stream.CopyToAsync(peer, stopping.Token);
                var download = peer.CopyToAsync(stream, stopping.Token);
                await Task.WhenAny(upload, download);
                incoming.Dispose(); outgoing.Dispose();
                try { await Task.WhenAll(upload, download); } catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { incoming.Dispose(); }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopping.Cancel();
            listener.Stop();
            foreach (var client in clients) client.Dispose();
        }
    }
}

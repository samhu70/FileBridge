using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using FileBridge.Core;

namespace FileBridge.Tests;

internal static class RetryChecks
{
    public static async Task RunAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize * 3 + 73);
        var source = Path.Combine(root, "persistent-source.bin");
        await File.WriteAllBytesAsync(source, bytes, ct);
        var destination = Path.Combine(root, "persistent-received");
        var interrupted = false;
        var failedResumes = 0;
        await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, destination, new InlineProgress(p =>
        {
            if (!interrupted && p.Stage == TransferStage.Receiving && p.Completed >= Wire.ChunkSize * 2L)
            { interrupted = true; throw new TransferException(503, "temporary interruption after disk commit"); }
            if (interrupted && p.Stage == TransferStage.VerifyingResume && Interlocked.Increment(ref failedResumes) <= 10)
                throw new TransferException(503, "receiver still unavailable");
        }));
        var retries = new HashSet<int>();
        var resumed = false;
        await TransferSender.SendAsync([new(source, "中文/resumed.bin", bytes.Length)], receiver.Invitation.Encode(), new InlineProgress(p =>
        {
            if (p.Stage == TransferStage.Retrying)
            {
                Check(p.Completed == Wire.ChunkSize && p.BytesPerSecond == 0 && p.RetryLimit == 0,
                    "reconnection lost acknowledged progress or advertised a retry limit");
                Check(p.RetryDelaySeconds is >= 0 and <= 15, "retry delay exceeded its cap");
                if (retries.Add(p.RetryAttempt)) Console.WriteLine("WAIT persistent retry " + p.RetryAttempt);
            }
            if (interrupted && p.Stage == TransferStage.VerifyingResume && p.Completed == Wire.ChunkSize * 2L) resumed = true;
        }), ct, 1);
        Check(retries.SetEquals(Enumerable.Range(1, 11)) && failedResumes == 11 && resumed,
            "sender stopped at the old ten-attempt limit or did not verify the durable prefix");
        Check(await Wire.FileHashAsync(Path.Combine(destination, "中文/resumed.bin"), ct) == Wire.Hash(bytes), "persistent retry checksum mismatch");
        Console.WriteLine("PASS eleven consecutive interruptions / no retry limit / acknowledged progress retained / verified resume / SHA-256");
        await CancellationAndPermanentErrorsAsync(root, source, receiver.Invitation, ct);
    }

    private static async Task CancellationAndPermanentErrorsAsync(string root, string source, Invitation invitation, CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var offline = invitation with { Url = $"https://127.0.0.1:{port}" };
        using var paused = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pauseClock = new Stopwatch();
        var attempts = new HashSet<int>();
        try
        {
            await TransferSender.SendAsync([new(source, "offline.bin", new FileInfo(source).Length)], offline.Encode(), new InlineProgress(p =>
            {
                if (p.Stage != TransferStage.Retrying) return;
                attempts.Add(p.RetryAttempt);
                if (p.RetryAttempt == 2 && p.RetryDelaySeconds == 2) { pauseClock.Start(); paused.CancelAfter(100); }
            }), paused.Token);
            throw new Exception("pause did not cancel continuous reconnection");
        }
        catch (OperationCanceledException) when (paused.IsCancellationRequested)
        { Check(pauseClock.IsRunning && pauseClock.Elapsed < TimeSpan.FromSeconds(1) && attempts.SetEquals([1, 2]), "pause did not cancel backoff promptly"); }
        Console.WriteLine("PASS offline connection retries / pause cancels backoff promptly / no extra attempt");

        foreach (var bad in new[] { invitation with { Token = new string('0', 64) }, invitation with { Pin = new string('0', 64) } })
        {
            var retry = false;
            try
            {
                await TransferSender.SendAsync([new(source, "denied.bin", new FileInfo(source).Length)], bad.Encode(),
                    new InlineProgress(p => retry |= p.Stage == TransferStage.Retrying), ct);
                throw new Exception("invalid credentials were accepted");
            }
            catch (TransferException error) when (error.Status == 401) { }
            catch (HttpRequestException error) when (error.InnerException is AuthenticationException) { }
            Check(!retry, "authentication or TLS failure triggered infinite retry");
        }
        var changed = Path.Combine(root, "retry-changing.txt");
        await File.WriteAllTextAsync(changed, "original", ct);
        try
        {
            await TransferSender.SendAsync([new(changed, "changed.txt", 8)], offline.Encode(), new InlineProgress(p =>
            {
                if (p.Stage == TransferStage.Retrying && p.RetryDelaySeconds == 1) File.WriteAllText(changed, "different length");
            }), ct);
            throw new Exception("changed source was retried indefinitely");
        }
        catch (IOException error) when (error.Message == "源文件发生变化，请停止编辑后重新发送。") { }
        Console.WriteLine("PASS invalid token and TLS pin stop immediately / source change stops retry safely");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using FileBridge.Core;

namespace FileBridge.Tests;

internal static class ReceiveDisconnectChecks
{
    public static async Task RunAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        var waiting = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fatalError = false;
        var directory = Path.Combine(root, "truncated-request");
        await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, directory, new InlineProgress(p =>
        {
            if (p.Stage == TransferStage.WaitingForSender) waiting.TrySetResult(p);
            if (p.Stage == TransferStage.ReceiveError) fatalError = true;
        }));
        var invite = receiver.Invitation;
        var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize + 8192);
        var manifest = new FileManifest("truncated.bin", bytes.Length, Wire.Hash(bytes));
        using var client = TransferSender.CreateClient(invite);
        using var metadata = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(manifest));
        metadata.Headers.ContentType = new("application/json");
        await TransferSender.RequestAsync(client, "v2/begin", metadata, ct);
        await TransferSender.RequestAsync(client, $"v2/chunk?id={manifest.Id}&offset=0", new ByteArrayContent(bytes[..Wire.ChunkSize]),
            ct, Wire.Hash(bytes.AsSpan(0, Wire.ChunkSize)));
        using (var socket = new TcpClient())
        {
            var uri = new Uri(invite.Url);
            await socket.ConnectAsync(uri.Host, uri.Port, ct);
            using var tls = new SslStream(socket.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var copy = new X509Certificate2(certificate);
                return TransferSender.CertificateMatches(invite, copy);
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.Host,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, ct);
            var headers = $"POST /v2/chunk?id={manifest.Id}&offset={Wire.ChunkSize} HTTP/1.1\r\nHost: {uri.Authority}\r\n"
                + $"Authorization: Bearer {invite.Token}\r\nX-Chunk-SHA256: {Wire.Hash(bytes.AsSpan(Wire.ChunkSize))}\r\nContent-Length: 8192\r\nConnection: close\r\n\r\n";
            await tls.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
            await tls.WriteAsync(bytes.AsMemory(Wire.ChunkSize, 1024), ct);
            await tls.FlushAsync(ct);
            await tls.ShutdownAsync(); // Valid TLS close, but an incomplete Content-Length body reproduces the reported error.
            var progress = await waiting.Task.WaitAsync(ct);
            Check(progress.Completed == Wire.ChunkSize && progress.Total == bytes.Length && progress.BytesPerSecond == 0,
                "truncated request lost the verified prefix or total size");
            var response = new byte[4096];
            try
            {
                var count = await tls.ReadAsync(response, ct);
                Check(count == 0 || Encoding.ASCII.GetString(response, 0, count).StartsWith("HTTP/1.1 408"),
                    "truncated body returned a non-retryable HTTP response");
            }
            catch (IOException) { } // A peer that closes TLS may reset TCP before the server can return HTTP 408.
        }
        Check(!fatalError && new FileInfo(Path.Combine(directory, Wire.PartsDirectory, manifest.Id + ".part")).Length == Wire.ChunkSize,
            "incomplete request was reported as fatal or wrote unverified data");
        var source = Path.Combine(root, "truncated-source.bin");
        await File.WriteAllBytesAsync(source, bytes, ct);
        var resumed = false;
        await TransferSender.SendAsync([new(source, manifest.Name, bytes.Length)], invite.Encode(), new InlineProgress(p =>
        {
            if (p.Stage == TransferStage.VerifyingResume && p.Completed == Wire.ChunkSize) resumed = true;
        }), ct, 1);
        Check(resumed && await Wire.FileHashAsync(Path.Combine(directory, manifest.Name), ct) == manifest.Hash,
            "receiver did not remain available for verified resume after a truncated request");
        Console.WriteLine("PASS actual truncated TLS request body / recoverable disconnect / waiting state keeps progress / partial chunk discarded / verified resume");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

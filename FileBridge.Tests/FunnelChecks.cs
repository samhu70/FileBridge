using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FileBridge.Core;
using FileBridge.Desktop;

namespace FileBridge.Tests;

// Explicit opt-in: temporarily exposes only an isolated, authenticated FileBridge test receiver.
internal static class FunnelChecks
{
    public static async Task RunAsync(string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = deadline.Token;
        var before = await ConfigurationAsync(ct);
        await using var receiver = await TransferReceiver.StartAsync(Wire.DiscoverAddress(), 0, Path.Combine(root, "public-received"), loopback: true);
        var lease = await TailscaleFunnel.StartAsync(receiver.LoopbackPort, null, ct);
        var publicUri = new Uri(lease.Url);
        try
        {
            var invite = receiver.Invitation with { Url = "https://127.0.0.1:1", PublicUrl = lease.Url };
            var source = Path.Combine(root, "public-source.txt");
            await File.WriteAllTextAsync(source, "one code, pinned public fallback", ct);
            await TransferSender.SendAsync([new(source, "public.txt", new FileInfo(source).Length)], invite.Encode(), null, ct);
            Check(await File.ReadAllTextAsync(Path.Combine(root, "public-received", "public.txt"), ct) == "one code, pinned public fallback", "public transfer mismatch");
            var address = await PublicAddressAsync(publicUri.Host, ct);
            var octets = address.GetAddressBytes();
            Check(octets is not [100, >= 64 and <= 127, _, _] and not [127, _, _, _] and not [10, _, _, _]
                and not [192, 168, _, _] and not [172, >= 16 and <= 31, _, _], "public DNS returned a private address");
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(address, publicUri.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            };
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var copy = new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate);
                return TransferSender.CertificateMatches(invite, copy);
            };
            using var external = new HttpClient(handler) { BaseAddress = publicUri, Timeout = TimeSpan.FromSeconds(20) };
            using (var denied = await external.PostAsync("/v2/ping", null, ct))
                Check(denied.StatusCode == HttpStatusCode.Unauthorized, "public relay permitted unauthenticated service access");
            external.DefaultRequestHeaders.Authorization = new("Bearer", invite.Token);
            await TransferSender.RequestAsync(external, "v2/ping", null, ct);
            Console.WriteLine("PASS real Funnel public DNS / public relay TCP and pinned end-to-end TLS / bearer enforcement / one-code transfer");
        }
        finally { await lease.DisposeAsync(); }
        await WaitConfigurationAsync(before, ct);
        Console.WriteLine("PASS stopping public receive removes only its own Funnel and preserves existing services");

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["FILEBRIDGE_TEST_ROOT"] = root;
        foreach (var arg in new[] { typeof(FunnelChecks).Assembly.Location, "--funnel-crash-owner" }) start.ArgumentList.Add(arg);
        using var owner = Process.Start(start)!;
        var errors = owner.StandardError.ReadToEndAsync();
        try
        {
            var ready = await owner.StandardOutput.ReadLineAsync(ct);
            Check(ready == "FUNNEL READY", "crash-test owner could not start its isolated Funnel: " + ready);
            owner.Kill(entireProcessTree: false);
            await owner.WaitForExitAsync(ct);
            await WaitConfigurationAsync(before, ct);
        }
        finally
        {
            if (!owner.HasExited) { owner.Kill(entireProcessTree: true); await owner.WaitForExitAsync(ct); }
            _ = await errors;
        }
        Console.WriteLine("PASS abrupt owner process termination / stdin leash cleanup / existing services preserved");
    }

    internal static async Task CrashOwnerAsync(string root)
    {
        await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, Path.Combine(root, "crash-owner"), loopback: true);
        await using var funnel = await TailscaleFunnel.StartAsync(receiver.LoopbackPort, null);
        Console.WriteLine("FUNNEL READY");
        await Task.Delay(TimeSpan.FromMinutes(1));
    }

    private static async Task<IPAddress> PublicAddressAsync(string host, CancellationToken ct)
    {
        using var dns = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        dns.DefaultRequestHeaders.Accept.ParseAdd("application/dns-json");
        for (var attempt = 0; attempt < 65; attempt++)
        {
            using var lookup = await dns.GetFromJsonAsync<JsonDocument>("https://cloudflare-dns.com/dns-query?type=A&name=" + host, ct);
            if (lookup is not null && lookup.RootElement.TryGetProperty("Answer", out var answers))
                foreach (var answer in answers.EnumerateArray())
                    if (answer.GetProperty("type").GetInt32() == 1) return IPAddress.Parse(answer.GetProperty("data").GetString()!);
            if (attempt % 6 == 0) Console.WriteLine("WAIT public DNS propagation (Funnel remains isolated to the test receiver)");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        throw new Exception("Funnel public DNS did not propagate within eleven minutes");
    }

    private static async Task<string> ConfigurationAsync(CancellationToken ct)
    {
        var cli = TailscaleFunnel.FindExecutable();
        using var process = Process.Start(new ProcessStartInfo(cli) { ArgumentList = { "serve", "status", "--json" },
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        _ = await errors;
        return (await output).Trim();
    }
    private static async Task WaitConfigurationAsync(string expected, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await ConfigurationAsync(ct) == expected) return;
            await Task.Delay(100, ct);
        }
        throw new Exception("Funnel cleanup did not restore the original Serve configuration");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}

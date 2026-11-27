using System.Globalization;
using System.Net;
using FileBridge.Core;

namespace FileBridge.Cli;

internal static class CliApp
{
    private const string Help = """
        FileBridge CLI — headless, resumable transfers compatible with FileBridge Desktop

        filebridge receive [--directory PATH] [--address IP] [--port PORT] [--config FILE]
        filebridge receive --code CODE [--directory PATH] [--parallel 1..10]
        filebridge send --code CODE [--parallel 1..10] [--] PATH [PATH ...]

        receive: print the connection code to stdout; progress and errors go to stderr.
          --address   Concrete local IP (default: saved IP, then Tailscale/LAN discovery).
          --port      Default: saved port, then 8787. Use 0 to allocate and save a free port.
          --directory Save directory (default: current working directory on every start).
          --code      Reverse receive: connect to the peer and wait for its reverse-send offer.
                      Use - to read the code from stdin. No local listener is opened.
                      Receives one offered batch, then exits. Ctrl+C stops waiting safely.
          --parallel  Reverse receive safety cap, 1..10 (default: follow sender, up to 10).
          --config    Receiver credentials/settings (default: FileBridge/cli/connections.json
                      under the current user's local application data directory).
        send:
          --code      Complete fb2_/fb3_ code from a CLI or desktop receiver.
                      Use --code - to read it from stdin without a command-line argument.
          --parallel  Maximum concurrent chunks, 1..10 (default: 4).

        Ctrl+C / SIGTERM stops safely. Rerun the same send command to resume verified data.
        Keep the receiver's config and destination's .filebridge-parts directory.
        Exit codes: 0 success/receiver stopped, 1 transfer/startup error, 2 usage error,
                    130 sender interrupted. No graphical session is required.
        """;

    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var receiving = args.FirstOrDefault() == "receive";
        try
        {
            if (args.Length == 0 || args is ["--help"] or ["-h"]
                || args is ["send" or "receive", "--help" or "-h"])
            { await output.WriteLineAsync(Help); return 0; }
            if (args[0] is not ("send" or "receive")) throw new UsageException("Expected 'send' or 'receive'. Use --help.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var paths = new List<string>();
            var positional = false;
            for (var i = 1; i < args.Length; i++)
            {
                var arg = args[i];
                if (!positional && arg == "--") { positional = true; continue; }
                if (!positional && arg.StartsWith('-'))
                {
                    var allowed = receiving ? arg is "--directory" or "--address" or "--port" or "--config" or "--code" or "--parallel"
                        : arg is "--code" or "--parallel";
                    if (!allowed) throw new UsageException("Unknown option: " + arg);
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new UsageException("Missing value for " + arg);
                    if (!options.TryAdd(arg, args[++i])) throw new UsageException("Duplicate option: " + arg);
                }
                else paths.Add(arg);
            }

            var progress = new ConsoleProgress(error);
            if (!receiving)
            {
                if (!options.TryGetValue("--code", out var code) || string.IsNullOrWhiteSpace(code) || paths.Count == 0)
                    throw new UsageException("send requires --code and at least one file or directory.");
                var parallel = Number(options.GetValueOrDefault("--parallel", "4"), 1, Wire.MaxParallelChunks, "--parallel");
                code = await ReadCodeAsync(code, error, ct);
                var files = TransferSender.Collect(paths);
                await TransferSender.SendAsync(files, code, progress, ct, parallel);
                await error.WriteLineAsync("Transfer complete; SHA-256 verified.");
                return 0;
            }

            if (paths.Count > 0) throw new UsageException("receive accepts options only. Use --directory PATH.");
            var directory = options.GetValueOrDefault("--directory", Environment.CurrentDirectory);
            if (string.IsNullOrWhiteSpace(directory)) throw new UsageException("--directory cannot be empty.");
            directory = Path.GetFullPath(directory);
            if (options.TryGetValue("--code", out var reverseCode))
            {
                if (options.ContainsKey("--address") || options.ContainsKey("--port") || options.ContainsKey("--config"))
                    throw new UsageException("Reverse receive cannot use --address, --port or --config; it connects to the peer without a local listener.");
                var parallel = Number(options.GetValueOrDefault("--parallel", "10"), 1, Wire.MaxParallelChunks, "--parallel");
                reverseCode = await ReadCodeAsync(reverseCode, error, ct);
                await error.WriteLineAsync($"Reverse receiving into {directory}; waiting for the peer to reverse-send files. Stop with Ctrl+C.");
                await ReverseTransfer.ReceiveAsync(reverseCode, directory, progress, ct, parallel);
                await error.WriteLineAsync("Reverse transfer complete; SHA-256 verified.");
                return 0;
            }
            if (options.ContainsKey("--parallel")) throw new UsageException("receive --parallel requires --code for reverse receiving.");
            var config = options.GetValueOrDefault("--config", Path.Combine(Path.GetDirectoryName(ConnectionStore.DefaultPath)!, "cli", "connections.json"));
            if (string.IsNullOrWhiteSpace(config)) throw new UsageException("--config cannot be empty.");
            var store = new ConnectionStore(config);
            using var lease = store.AcquireReceiverLease();
            var saved = store.Load();
            var address = options.GetValueOrDefault("--address", string.IsNullOrEmpty(saved.Address) ? Wire.DiscoverAddress() : saved.Address);
            if (!IPAddress.TryParse(address, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                throw new UsageException("--address must be a concrete local IP, not 0.0.0.0 or ::.");
            var port = Number(options.GetValueOrDefault("--port", saved.Port), 0, 65535, "--port");
            var identity = store.GetIdentity();
            await using var receiver = await TransferReceiver.StartAsync(address, port, directory, progress, ct, identity);
            store.SavePreferences(saved with { Address = address, Port = new Uri(receiver.Invitation.Url).Port.ToString(CultureInfo.InvariantCulture), Directory = directory });
            await error.WriteLineAsync($"Receiving at {receiver.Invitation.Url} into {directory}. Stop with Ctrl+C.");
            if (ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback))
                await error.WriteLineAsync("Loopback address: only this computer can connect. Use --address with a reachable LAN/Tailscale IP for another computer.");
            await output.WriteLineAsync(receiver.Invitation.Encode());
            await output.FlushAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await error.WriteLineAsync("Stopped. Verified partial data is retained for resume.");
            return receiving ? 0 : 130;
        }
        catch (UsageException ex) { await error.WriteLineAsync(ex.Message); return 2; }
        catch (Exception ex) { await error.WriteLineAsync("Error: " + ex.Message); return 1; }
    }

    private static async Task<string> ReadCodeAsync(string code, TextWriter error, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new UsageException("--code cannot be empty.");
        if (code == "-")
        {
            await error.WriteLineAsync("Reading connection code from stdin...");
            // Console input can block synchronously even through ReadLineAsync.
            code = await Task.Run(() => Console.In.ReadLine(), ct).WaitAsync(ct) ?? "";
        }
        Invitation.Parse(code);
        return code;
    }

    private static int Number(string value, int min, int max, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < min || number > max)
            throw new UsageException($"{option} must be {min}..{max}.");
        return number;
    }

    private sealed class UsageException(string message) : Exception(message);

    private sealed class ConsoleProgress(TextWriter writer) : IProgress<TransferProgress>
    {
        private readonly object gate = new();
        private long last;
        private TransferStage? stage;
        private string? file;

        public void Report(TransferProgress value)
        {
            lock (gate)
            {
                var now = Environment.TickCount64;
                if (value.Stage == stage && value.FileName == file && now - last < 1000 && value.Error is null) return;
                last = now; stage = value.Stage; file = value.FileName;
                // Keep untrusted file names and peer errors on one terminal line.
                static string Safe(string text) => string.Concat(text.Select(c => char.IsControl(c) ? ' ' : c));
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{value.Stage}: {Safe(value.FileName)} {value.Completed}/{value.Total} bytes {value.BytesPerSecond:F0} B/s")
                    + (value.Error is null ? "" : $" ({Safe(value.Error)})"));
            }
        }
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FileBridge.Cli;
using FileBridge.Core;

var root = Path.Combine(Path.GetTempPath(), "filebridge-cli-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = timeout.Token;
try
{
    foreach (var arguments in new string[][] {
        ["oops"], ["send"], ["receive", "extra"], ["send", "--code"],
        ["send", "--code", "x", "--parallel", "11", "x"], ["send", "--bogus", "x"],
        ["receive", "--directory", root, "--port", "65536"],
        ["receive", "--directory", root, "--address", "0.0.0.0"],
        ["receive", "--parallel", "2"], ["receive", "--code", "x", "--parallel", "0"],
        ["receive", "--code", "x", "--port", "8787"],
        ["send", "--code", "x", "--code", "y", "x"] })
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        // Invalid receives use isolated state, never the user's real receiver profile.
        var input = arguments[0] == "receive" && !arguments.Contains("--code")
            ? arguments.Concat(["--config", Path.Combine(root, "invalid", "connections.json")]).ToArray() : arguments;
        Check(await CliApp.RunAsync(input, output, error, ct) == 2 && output.ToString() == "" && error.ToString().Length > 0,
            "invalid arguments were not rejected");
    }
    using (var help = new StringWriter())
        Check(await CliApp.RunAsync(["--help"], help, TextWriter.Null, ct) == 0 && help.ToString().Contains("receive"), "help failed");
    Console.WriteLine("PASS help / missing, unknown, duplicate and invalid options");

    var defaultSource = Path.Combine(root, "current-directory.txt");
    await File.WriteAllTextAsync(defaultSource, "Current working directory", ct);
    var defaultConfig = Path.Combine(root, "cwd-profile", "connections.json");
    string? previousCode = null;
    foreach (var name in new[] { "first-cwd", "second-cwd" })
    {
        var workingDirectory = Path.Combine(root, name);
        Directory.CreateDirectory(workingDirectory);
        await using var receiver = new CliProcess(
            ["receive", "--port", previousCode is null ? "0" : new Uri(Invitation.Parse(previousCode).Url).Port.ToString(), "--config", defaultConfig],
            workingDirectory);
        var currentCode = await receiver.ReadCodeAsync(ct);
        Check(previousCode is null || currentCode == previousCode, "working directory change rotated connection code");
        await TransferSender.SendAsync(TransferSender.Collect([defaultSource]), currentCode, null, ct);
        Check(await File.ReadAllTextAsync(Path.Combine(workingDirectory, "current-directory.txt"), ct) == "Current working directory",
            "receive did not default to process working directory");
        previousCode = currentCode;
        if (!OperatingSystem.IsWindows()) await receiver.StopAsync(ct);
    }
    Console.WriteLine("PASS omitted address and directory / automatic local address / first-use current directory / changed current directory overrides saved destination / stable code");

    var source = Path.Combine(root, "源目录");
    Directory.CreateDirectory(Path.Combine(source, "子目录"));
    var bytes = RandomNumberGenerator.GetBytes(2 * Wire.ChunkSize + 123);
    var large = Path.Combine(source, "子目录", "断点.bin");
    await File.WriteAllBytesAsync(large, bytes, ct);
    await File.WriteAllTextAsync(Path.Combine(source, "空文件.txt"), "", ct);
    var files = TransferSender.Collect([source]);
    var largeFile = files.Single(f => f.Size > 0);
    var manifest = new FileManifest(largeFile.RelativeName, bytes.Length, Wire.Hash(bytes));
    var destination = Path.Combine(root, "cli-received");
    var config = Path.Combine(root, "receiver-state", "connections.json");

    string code;
    await using (var receiver = new CliProcess("receive", "--directory", destination, "--address", "127.0.0.1", "--port", "0", "--config", config))
    {
        code = await receiver.ReadCodeAsync(ct);
        await using (var duplicate = new CliProcess("receive", "--config", config))
            Check(await duplicate.ExitAsync(ct) == 1, "duplicate receiver profile accepted");
        using var client = TransferSender.CreateClient(Invitation.Parse(code));
        await Begin(client, manifest);
        var first = bytes[..Wire.ChunkSize];
        await TransferSender.RequestAsync(client, $"v2/chunk?id={manifest.Id}&offset=0", new ByteArrayContent(first), ct, Wire.Hash(first));
        if (!OperatingSystem.IsWindows()) await receiver.StopAsync(ct, "-INT");
    }
    if (!OperatingSystem.IsWindows())
    {
        Check(File.GetUnixFileMode(config) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "credential permissions");
        Check(File.GetUnixFileMode(Path.GetDirectoryName(config)!) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "config directory permissions");
    }
    await using (var receiver = new CliProcess("receive", "--config", config, "--directory", destination))
    {
        Check(await receiver.ReadCodeAsync(ct) == code, "restart changed connection code");
        using var client = TransferSender.CreateClient(Invitation.Parse(code));
        Check((await Begin(client, manifest)).Offset == Wire.ChunkSize, "receiver restart lost verified offset");
        // Desktop uses these exact Core entry points; no graphical session is involved.
        await TransferSender.SendAsync(files, code, null, ct);
        foreach (var file in files)
            Check(await Wire.FileHashAsync(Path.Combine(destination, file.RelativeName), ct) == await Wire.FileHashAsync(file.LocalPath, ct), "desktop sender -> CLI checksum");
        var cliSource = Path.Combine(root, "cli-to-cli.txt");
        await File.WriteAllTextAsync(cliSource, "CLI to CLI", ct);
        await using (var sender = new CliProcess("send", "--code", code, cliSource))
            Check(await sender.ExitAsync(ct) == 0 && await File.ReadAllTextAsync(Path.Combine(destination, "cli-to-cli.txt"), ct) == "CLI to CLI", "CLI -> CLI transfer");
        if (!OperatingSystem.IsWindows()) await receiver.StopAsync(ct);
    }
    Console.WriteLine("PASS desktop Core -> CLI / CLI -> CLI / Unicode directory / empty file / receiver SIGINT+SIGTERM and restart / stable code / resume / permissions / profile lock");

    var desktopDestination = Path.Combine(root, "desktop-received");
    await using (var desktop = await TransferReceiver.StartAsync("127.0.0.1", 0, desktopDestination, ct: ct))
    {
        using var client = TransferSender.CreateClient(desktop.Invitation);
        await Begin(client, manifest);
        var first = bytes[..Wire.ChunkSize];
        await TransferSender.RequestAsync(client, $"v2/chunk?id={manifest.Id}&offset=0", new ByteArrayContent(first), ct, Wire.Hash(first));
        await using (var sender = new CliProcess("send", "--code", "-", "--parallel", "2", "--", source))
        {
            await sender.InputAsync(desktop.Invitation.Encode());
            Check(await sender.ExitAsync(ct) == 0, "CLI sender failed: " + sender.Errors);
            Check(sender.Errors.Contains($"VerifyingResume: {manifest.Name} {Wire.ChunkSize}/"), "CLI did not resume verified prefix");
            Check(await sender.ReadOutputAsync() == "", "sender polluted stdout");
        }
        foreach (var file in files)
            Check(await Wire.FileHashAsync(Path.Combine(desktopDestination, file.RelativeName), ct) == await Wire.FileHashAsync(file.LocalPath, ct), "CLI -> desktop checksum");
        await using (var again = new CliProcess("send", "--code", desktop.Invitation.Encode(), source))
            Check(await again.ExitAsync(ct) == 0 && again.Errors.Contains("AlreadyComplete:"), "completed files not skipped");
        var rejected = desktop.Invitation with { Token = new string('0', 64) };
        await using (var unauthorized = new CliProcess("send", "--code", rejected.Encode(), large))
            Check(await unauthorized.ExitAsync(ct) == 1, "bad token did not fail");
        var badPin = desktop.Invitation with { Pin = new string('0', 64) };
        await using (var untrusted = new CliProcess("send", "--code", badPin.Encode(), large))
            Check(await untrusted.ExitAsync(ct) == 1, "bad TLS pin did not fail");
        await File.WriteAllTextAsync(Path.Combine(desktopDestination, "conflict.txt"), "original", ct);
        var conflict = Path.Combine(root, "conflict.txt");
        await File.WriteAllTextAsync(conflict, "different", ct);
        await using (var collision = new CliProcess("send", "--code", desktop.Invitation.Encode(), conflict))
            Check(await collision.ExitAsync(ct) == 1 && await File.ReadAllTextAsync(Path.Combine(desktopDestination, "conflict.txt"), ct) == "original", "existing file overwritten");
    }
    Console.WriteLine("PASS CLI -> desktop Core / stdin code / bounded parallel send / verified resume / completed skip / authentication / TLS pin / no overwrite");

    // Interrupt a sender waiting to reconnect, then resume it after the receiver restarts.
    if (!OperatingSystem.IsWindows())
    {
        await using (var waiting = new CliProcess("send", "--code", "-", large))
        {
            await waiting.WaitForErrorAsync("Reading connection code", ct);
            await waiting.SignalAsync();
            Check(await waiting.ExitAsync(ct) == 130, "stdin code read blocked sender cancellation");
        }
        var interruptedDest = Path.Combine(root, "interrupted");
        var identity = ReceiverIdentity.Create();
        var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, interruptedDest, ct: ct, identity: identity);
        var invite = receiver.Invitation;
        using (var client = TransferSender.CreateClient(invite))
        {
            await Begin(client, manifest);
            var first = bytes[..Wire.ChunkSize];
            await TransferSender.RequestAsync(client, $"v2/chunk?id={manifest.Id}&offset=0", new ByteArrayContent(first), ct, Wire.Hash(first));
        }
        await receiver.DisposeAsync();
        await using (var sender = new CliProcess("send", "--code", invite.Encode(), source))
        {
            await sender.WaitForErrorAsync("Retrying:", ct);
            await sender.SignalAsync();
            Check(await sender.ExitAsync(ct) == 130, "interrupted sender exit status");
        }
        await using var restarted = await TransferReceiver.StartAsync("127.0.0.1", new Uri(invite.Url).Port, interruptedDest, ct: ct, identity: identity);
        await using var resumed = new CliProcess("send", "--code", invite.Encode(), source);
        Check(await resumed.ExitAsync(ct) == 0 && resumed.Errors.Contains($"VerifyingResume: {manifest.Name} {Wire.ChunkSize}/"), "interrupted send did not resume");
        Check(await Wire.FileHashAsync(Path.Combine(interruptedDest, manifest.Name), ct) == manifest.Hash, "interrupted resume checksum");
        Console.WriteLine("PASS sender SIGTERM during stdin read and retry / exit 130 / restart and verified resume");
    }

    await ReverseCliChecks.RunAsync(root, ct);

    var corruptConfig = Path.Combine(root, "corrupt-state", "connections.json");
    Directory.CreateDirectory(Path.GetDirectoryName(corruptConfig)!);
    await File.WriteAllTextAsync(corruptConfig, "broken credentials", ct);
    await using (var broken = new CliProcess("receive", "--config", corruptConfig, "--directory", destination))
        Check(await broken.ExitAsync(ct) == 1 && await File.ReadAllTextAsync(corruptConfig, ct) == "broken credentials", "corrupt credentials silently replaced");
    Console.WriteLine("ALL CLI PASS");
}
catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
finally { Directory.Delete(root, recursive: true); }

async Task<ResumeState> Begin(HttpClient client, FileManifest manifest)
{
    using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(manifest));
    content.Headers.ContentType = new("application/json");
    return await TransferSender.RequestAsync(client, "v2/begin", content, ct);
}

static void Check(bool value, string message) { if (!value) throw new Exception(message); }

sealed class CliProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task errors;
    private readonly System.Text.StringBuilder errorText = new();
    public string Errors { get { lock (errorText) return errorText.ToString(); } }

    public CliProcess(params string[] args) : this(args, Environment.CurrentDirectory) { }

    public CliProcess(string[] args, string workingDirectory)
    {
        var executable = Environment.GetEnvironmentVariable("FILEBRIDGE_CLI_EXECUTABLE");
        var start = new ProcessStartInfo(executable ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, WorkingDirectory = workingDirectory };
        var prefix = executable is null ? new[] { "exec", "--runtimeconfig", Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".runtimeconfig.json"), typeof(CliApp).Assembly.Location } : [];
        foreach (var arg in prefix.Concat(args))
            start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new Exception("CLI process failed to start");
        errors = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                lock (errorText) errorText.AppendLine(line);
        });
    }
    public async Task<string> ReadCodeAsync(CancellationToken ct)
    {
        var line = await process.StandardOutput.ReadLineAsync(ct) ?? throw new Exception("receiver emitted no code: " + Errors);
        _ = Invitation.Parse(line);
        return line;
    }
    public async Task<int> ExitAsync(CancellationToken ct)
    { await process.WaitForExitAsync(ct); await errors.WaitAsync(ct); return process.ExitCode; }
    public async Task InputAsync(string line) { await process.StandardInput.WriteLineAsync(line); process.StandardInput.Close(); }
    public Task<string> ReadOutputAsync() => process.StandardOutput.ReadToEndAsync();
    public async Task SignalAsync(string signalName = "-TERM")
    {
        var start = new ProcessStartInfo("kill") { UseShellExecute = false };
        start.ArgumentList.Add(signalName); start.ArgumentList.Add(process.Id.ToString());
        using var signal = Process.Start(start)!; await signal.WaitForExitAsync();
    }
    public async Task StopAsync(CancellationToken ct, string signalName = "-TERM")
    { await SignalAsync(signalName); if (await ExitAsync(ct) != 0) throw new Exception("receiver did not stop gracefully: " + Errors); }
    public async Task WaitForErrorAsync(string text, CancellationToken ct)
    {
        while (!Errors.Contains(text))
        { if (process.HasExited) throw new Exception("CLI exited early: " + Errors); await Task.Delay(25, ct); }
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(); await errors; process.Dispose();
    }
}

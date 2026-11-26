using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileBridge.Desktop;

internal sealed class FunnelSetupException(string message, Uri? setupUri = null) : Exception(message)
{
    public Uri? SetupUri { get; } = setupUri;
}

/// <summary>Own only an ephemeral TCP Funnel; never reset or replace another Tailscale service.</summary>
internal sealed class TailscaleFunnel : IAsyncDisposable
{
    private readonly Process helper;
    private readonly Task<string> output, errors;
    private bool disposed;
    public string Url { get; }

    private TailscaleFunnel(Process helper, string url)
    {
        this.helper = helper;
        Url = url;
        output = helper.StandardOutput.ReadToEndAsync();
        errors = helper.StandardError.ReadToEndAsync();
    }

    public static async Task<TailscaleFunnel> StartAsync(int localPort, string? preferredUrl, CancellationToken ct = default)
    {
        var executable = FindExecutable();
        using var status = JsonDocument.Parse(await RunAsync(executable, ["status", "--json"], ct));
        if (!status.RootElement.TryGetProperty("BackendState", out var state) || state.GetString() != "Running")
            throw new FunnelSetupException("请先登录并连接 Tailscale，再启用公网备用入口。");
        var host = status.RootElement.GetProperty("Self").GetProperty("DNSName").GetString()?.TrimEnd('.');
        if (host is null || !Regex.IsMatch(host, @"\A[a-z0-9-]+(?:\.[a-z0-9-]+)+\.ts\.net\z", RegexOptions.IgnoreCase))
            throw new FunnelSetupException("Tailscale 未提供可用域名，请先启用 MagicDNS。");

        using var configLease = await AcquireConfigLeaseAsync(ct);
        using var before = JsonDocument.Parse(await RunAsync(executable, ["serve", "status", "--json"], ct));
        var preferred = Uri.TryCreate(preferredUrl, UriKind.Absolute, out var uri) && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
            ? uri.Port : 0;
        var port = SelectPort(before.RootElement, preferred);
        var url = new UriBuilder("https", host, port).Uri.GetLeftPart(UriPartial.Authority);
        var start = HelperStartInfo();
        foreach (var argument in new[] { "--filebridge-funnel", executable, port.ToString(CultureInfo.InvariantCulture), localPort.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        var lease = new TailscaleFunnel(Process.Start(start) ?? throw new IOException("无法启动 Tailscale。"), url);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (!lease.helper.HasExited)
            {
                timeout.Token.ThrowIfCancellationRequested();
                using var current = JsonDocument.Parse(await RunAsync(executable, ["serve", "status", "--json"], timeout.Token));
                if (HasForwarder(current.RootElement, port, localPort, host)) return lease;
                await Task.Delay(150, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch { await lease.DisposeAsync(); throw; }
        await lease.DisposeAsync();
        ct.ThrowIfCancellationRequested();
        var details = await lease.output + "\n" + await lease.errors;
        var match = Regex.Match(details, @"https://login\.tailscale\.com/[^\s\""<>]+", RegexOptions.IgnoreCase);
        var setup = match.Success && Uri.TryCreate(match.Value.TrimEnd('.', ',', ')'), UriKind.Absolute, out var parsed) ? parsed : null;
        throw new FunnelSetupException("公网入口未能启动，请检查 Tailscale Funnel 授权、网络和可用端口。", setup);
    }

    internal static int SelectPort(JsonElement config, int preferred = 0)
    {
        foreach (var port in new[] { preferred, 8443, 10000, 443 }.Where(p => p is 443 or 8443 or 10000).Distinct())
            if (!PortUsed(config, port)) return port;
        throw new FunnelSetupException("公网入口端口均已被使用，请先停止不再需要的公网接收。");
    }

    private static bool PortUsed(JsonElement config, int port)
    {
        if (config.ValueKind != JsonValueKind.Object) return false;
        if (config.TryGetProperty("TCP", out var tcp) && tcp.TryGetProperty(port.ToString(CultureInfo.InvariantCulture), out _)) return true;
        return config.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object && PortUsed(p.Value, port));
    }

    internal static bool HasForwarder(JsonElement config, int port, int localPort, string host)
    {
        if (config.ValueKind != JsonValueKind.Object) return false;
        var key = port.ToString(CultureInfo.InvariantCulture);
        if (config.TryGetProperty("TCP", out var tcp) && tcp.TryGetProperty(key, out var handler)
            && handler.TryGetProperty("TCPForward", out var target) && target.GetString() == $"127.0.0.1:{localPort}"
            && !handler.TryGetProperty("TerminateTLS", out _)
            && config.TryGetProperty("AllowFunnel", out var allowed)
            && allowed.TryGetProperty($"{host}:{port}", out var enabled) && enabled.ValueKind == JsonValueKind.True) return true;
        return config.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object && HasForwarder(p.Value, port, localPort, host));
    }

    internal static string FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).ToList();
        if (OperatingSystem.IsWindows()) directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale"));
        else directories.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/Applications/Tailscale.app/Contents/MacOS"]);
        foreach (var directory in directories)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
            if (!OperatingSystem.IsWindows() && File.Exists(Path.Combine(directory, "Tailscale"))) return Path.Combine(directory, "Tailscale");
        }
        throw new FunnelSetupException("未找到 Tailscale，请在接收电脑安装并登录 Tailscale。");
    }

    private static async Task<FileStream> AcquireConfigLeaseAsync(CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(ConnectionStore.DefaultPath)!;
        Directory.CreateDirectory(folder);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(folder, ".funnel-config.lock"), options); }
            catch (IOException error) when (attempt < 50 && (error.HResult & 0xffff) is 11 or 32 or 35)
            { await Task.Delay(100, ct); }
        }
    }

    private static ProcessStartInfo HelperStartInfo()
    {
        var ownAssembly = typeof(Program).Assembly;
        var entry = Assembly.GetEntryAssembly()!;
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (entry != ownAssembly)
        {
            start.FileName = "dotnet";
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(entry.Location, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(entry.Location, ".deps.json"), ownAssembly.Location }) start.ArgumentList.Add(argument);
        }
        else if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(ownAssembly.Location);
        return start;
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Tailscale。");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
            throw;
        }
        if (process.ExitCode != 0) throw new FunnelSetupException("Tailscale 命令未成功，请检查应用是否正在运行并已登录。");
        _ = await errors;
        return await output;
    }

    // The helper's stdin pipe closes even if the UI process crashes. Killing the CLI then removes its foreground session.
    internal static async Task<int> RunHelperAsync(string executable, int port, int localPort)
    {
        if (port is not (443 or 8443 or 10000) || localPort is < 1 or > 65535) return 1;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "funnel", $"--tcp={port}", $"tcp://127.0.0.1:{localPort}" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Tailscale。");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var stderr = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        try { await Task.WhenAny(process.WaitForExitAsync(), Console.In.ReadToEndAsync()); }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
        return process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { if (!helper.HasExited) helper.StandardInput.Close(); }
        catch (IOException) { if (!helper.HasExited) helper.Kill(entireProcessTree: true); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await helper.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!helper.HasExited) helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
        await Task.WhenAll(output, errors);
        helper.Dispose();
    }
}

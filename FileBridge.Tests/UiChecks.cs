using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using FileBridge.Core;
using FileBridge.Desktop;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileBridge.Tests;

internal static class UiChecks
{
    public static string Root { get; private set; } = "";
    public static Exception? Failure { get; set; }

    public static void Run(string root)
    {
        Root = root;
        AppBuilder.Configure<UiTestApplication>().UsePlatformDetect()
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        if (Failure is not null) throw new Exception("Desktop regression failed.", Failure);
        Console.WriteLine("ALL UI PASS");
    }

    public static async Task CheckAsync(MainWindow window)
    {
        Check(SynchronizationContext.Current is AvaloniaSynchronizationContext, "UI context not installed");
        T Control<T>(string name) where T : Control => window.FindControl<T>(name)!;
        void Click(string name) => Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var folder = Path.Combine(Root, "selection");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            for (var i = 0; i < 1024; i++) File.WriteAllText(Path.Combine(folder, $"{i}.txt"), "selected");
        });
        var scan = window.AddPathsAsync([folder]);
        Check(!scan.IsCompleted && !Control<Button>("SendButton").IsEnabled
            && !Control<Button>("FilesButton").IsEnabled && !Control<Button>("PauseButton").IsEnabled,
            "scan did not yield with conflicting actions disabled");
        var heartbeat = false;
        Dispatcher.UIThread.Post(() => heartbeat = true);
        await scan;
        var list = Control<ItemsControl>("FileList");
        Check(heartbeat && list.Items.Count == 1024, "scan blocked the dispatcher or lost files");
        var firstRow = list.Items[0];
        await window.AddPathsAsync([]);
        Check(ReferenceEquals(firstRow, list.Items[0]), "cancelled picker rebuilt the selection");
        try
        {
            await window.AddPathsAsync([Path.Combine(Root, "missing")]);
            throw new Exception("invalid scan unexpectedly succeeded");
        }
        catch (IOException) { }
        Check(list.Items.Count == 1024 && ReferenceEquals(firstRow, list.Items[0])
            && Control<Button>("SendButton").IsEnabled && !Control<Button>("PauseButton").IsEnabled,
            "failed scan changed the selection or left controls disabled");
        Click("ClearButton");
        Console.WriteLine("PASS UI background collection / dispatcher responsiveness / scan failure preserves selection");

        var source = Path.Combine(Root, "中文.bin");
        await File.WriteAllBytesAsync(source, new byte[Wire.ChunkSize + 17]);
        var destination = Path.Combine(Root, "received");
        Control<TextBox>("ReceiveAddress").Text = "127.0.0.1";
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            Control<TextBox>("ReceivePort").Text = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
        }
        Control<TextBox>("ReceiveDirectory").Text = destination;
        Click("ReceiveButton");
        await Until(() => Control<Button>("CopyButton").IsEnabled, "receiver startup");
        var originalCode = Control<TextBox>("ReceiveCode").Text;
        Control<TextBox>("SendCode").Text = originalCode;
        await window.AddPathsAsync([source]);
        Click("SendButton");
        await Until(() => Control<Button>("SendButton").IsEnabled, "UI send completion");
        await Until(() => Control<ProgressBar>("ReceiveProgress").Value == 100, "receive completion progress");
        Check(Control<ProgressBar>("SendProgress").Value == 100, "send progress not dispatched to UI");
        Check(await Wire.FileHashAsync(source, default) == await Wire.FileHashAsync(Path.Combine(destination, "中文.bin"), default),
            "UI transfer checksum differs");
        Click("StopReceiveButton");
        await Until(() => Control<Button>("ReceiveButton").IsEnabled, "receiver shutdown");
        Click("ReceiveButton");
        await Until(() => Control<Button>("CopyButton").IsEnabled, "receiver restart");
        Check(Control<ProgressBar>("ReceiveProgress").Value == 0, "new session retained old receive progress");
        Check(Control<TextBox>("ReceiveCode").Text != originalCode, "restart reused the invitation");
        Console.WriteLine("PASS UI send/receive callbacks / checksum / stop and restart resets progress and invitation");

        // Inject a failing host into the real receiver, without adding test hooks to production code.
        var receiver = (TransferReceiver)typeof(MainWindow).GetField("receiver", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var appField = typeof(TransferReceiver).GetField("app", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalApp = (WebApplication)appField.GetValue(receiver)!;
        await originalApp.StopAsync();
        await originalApp.DisposeAsync();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostedService>(new FailingStopService());
        var failingApp = builder.Build();
        await failingApp.StartAsync();
        appField.SetValue(receiver, failingApp);
        Click("StopReceiveButton");
        await Until(() => Control<Button>("ReceiveButton").IsEnabled, "failed shutdown cleanup");
        var message = Control<TextBlock>("AppMessage");
        Check(message.Text?.Contains("simulated shutdown failure", StringComparison.Ordinal) == true
            && message.Foreground is ISolidColorBrush { Color: var color } && color == Color.Parse("#B43D37"),
            "stop success message replaced the shutdown error");
        Check(!Control<Button>("CopyButton").IsEnabled && Control<TextBox>("ReceiveCode").Text == ""
            && Control<TextBox>("ReceiveAddress").IsEnabled, "failed stop left receiver controls active");
        Console.WriteLine("PASS UI shutdown exception retained / error style / controls restored");
    }

    private static async Task Until(Func<bool> condition, string action)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (!condition()) await Task.Delay(20, timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out: " + action);
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private sealed class FailingStopService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => throw new IOException("simulated shutdown failure");
    }
}

public sealed class UiTestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new MainWindow();
        desktop.MainWindow = window;
        Dispatcher.UIThread.Post(async () =>
        {
            try { await UiChecks.CheckAsync(window); }
            catch (Exception error) { UiChecks.Failure = error; }
            finally { window.Close(); desktop.Shutdown(); }
        });
        base.OnFrameworkInitializationCompleted();
    }
}

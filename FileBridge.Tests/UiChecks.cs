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
using Avalonia.VisualTree;
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

        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        var originalWidth = window.Width;
        string[] layoutKeys = ["SendTab", "ReceiveTab", "OpenReceiveDirectory", "ReverseSend", "ReverseSendHelp",
            "ReverseReceiveMode", "ReversePeerCode", "ReverseCodeHint", "ReverseReceiveHelp"];
        try
        {
            foreach (var language in new[] { "en", "zh", "en" })
            {
                var text = new Localization(Path.Combine(Root, language + ".txt"),
                    System.Globalization.CultureInfo.GetCultureInfo(language));
                foreach (var key in layoutKeys) window.Resources["Text." + key] = text.Text(key);
                foreach (var width in new[] { window.MinWidth, originalWidth })
                {
                    window.Width = width;
                    for (var selected = 0; selected < tabs.Items.Count; selected++)
                    {
                        tabs.SelectedIndex = selected;
                        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                        foreach (var tab in tabs.Items.Cast<TabItem>())
                        {
                            var header = tab.GetVisualDescendants().OfType<TextBlock>().Single();
                            var measured = new FormattedText(header.Text!, System.Globalization.CultureInfo.InvariantCulture,
                                header.FlowDirection, new Typeface(header.FontFamily, header.FontStyle, header.FontWeight),
                                header.FontSize, header.Foreground);
                            Check(header.Bounds.Width + 1 >= measured.WidthIncludingTrailingWhitespace
                                && header.Bounds.Height + 1 >= measured.Height,
                                $"Tab header clipped: {language}, width {width}, {header.Text}, bounds {header.Bounds.Size}, text {measured.WidthIncludingTrailingWhitespace}x{measured.Height}");
                        }
                        if (selected == 1)
                        {
                            Check(Control<TextBox>("ReceiveDirectory").Bounds.Width >= 160
                                && Control<Button>("OpenReceiveDirectoryButton").IsEffectivelyVisible,
                                $"Receive folder controls do not fit: {language}, width {width}");
                            Control<CheckBox>("ReverseReceiveMode").IsChecked = true;
                            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                            Check(Control<TextBox>("ReverseReceiveCode").Bounds.Width >= 160
                                && !Control<Grid>("LocalReceiveEndpoint").IsVisible,
                                $"Reverse receive controls do not fit: {language}, width {width}");
                            Control<CheckBox>("ReverseReceiveMode").IsChecked = false;
                            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                        }
                    }
                }
            }
        }
        finally
        {
            window.Width = originalWidth;
            tabs.SelectedIndex = 0;
            foreach (var key in layoutKeys) window.Resources["Text." + key] = Localization.Current.Text(key);
        }
        Console.WriteLine("PASS UI full tab labels / English-Chinese switching / both selections / minimum and default widths");

        var originalDirectory = Control<TextBox>("ReceiveDirectory").Text;
        Control<TextBox>("ReceiveDirectory").Text = "  ";
        Click("OpenReceiveDirectoryButton");
        Check(Control<TextBlock>("AppMessage").Text == Localization.Current.Text("Core.ChooseSaveLocation"),
            "empty receive directory did not show a useful error");
        var missingDirectory = Path.Combine(Root, "not-created");
        Control<TextBox>("ReceiveDirectory").Text = missingDirectory;
        Click("OpenReceiveDirectoryButton");
        Check(Control<TextBlock>("AppMessage").Text == Localization.Current.Text("ReceiveDirectoryMissing")
            && !Directory.Exists(missingDirectory), "missing receive directory was silently created or ignored");
        Control<TextBox>("ReceiveDirectory").Text = originalDirectory;
        Console.WriteLine("PASS UI open receive folder / empty and missing destinations / localized errors");

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
        await Until(() => Control<Button>("StopReceiveButton").IsEnabled, "receiver startup");
        Check(Control<Button>("OpenReceiveDirectoryButton").IsEnabled && !Control<Button>("DestinationButton").IsEnabled,
            "opening the receive folder was disabled while receiving");
        var originalCode = Control<TextBox>("ReceiveCode").Text;
        Control<TextBox>("SendCode").Text = originalCode;
        await window.AddPathsAsync([source]);
        Click("SendButton");
        await Until(() => Control<Button>("SendButton").IsEnabled, "UI send completion");
        await Until(() => Control<ProgressBar>("ReceiveProgress").Value == 100, "receive completion progress");
        Check(Control<ProgressBar>("SendProgress").Value == 100, "send progress not dispatched to UI");
        Check(await Wire.FileHashAsync(source, default) == await Wire.FileHashAsync(Path.Combine(destination, "中文.bin"), default),
            "UI transfer checksum differs");
        Click("OpenReceiveDirectoryButton");
        await Until(() => Control<TextBlock>("AppMessage").Text == Localization.Current.Text("ReceiveDirectoryOpened"),
            "native receive folder launch");
        Console.WriteLine("PASS UI native receive folder launch while receiving");
        Click("StopReceiveButton");
        await Until(() => Control<Button>("ReceiveButton").IsEnabled, "receiver shutdown");
        Click("ReceiveButton");
        await Until(() => Control<Button>("StopReceiveButton").IsEnabled, "receiver restart");
        Check(Control<ProgressBar>("ReceiveProgress").Value == 0, "new session retained old receive progress");
        Check(Control<TextBox>("ReceiveCode").Text == originalCode, "restart replaced a valid saved invitation");
        Console.WriteLine("PASS UI send/receive callbacks / checksum / stop/restart resets progress and preserves invitation");

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
        Check(Control<Button>("CopyButton").IsEnabled && Control<TextBox>("ReceiveCode").Text == originalCode
            && Control<TextBox>("ReceiveAddress").IsEnabled, "failed stop left receiver controls active");
        Console.WriteLine("PASS UI shutdown exception retained / error style / controls restored");
        Click("RecreateCodeButton");
        await Until(() => Control<Button>("RecreateCodeButton").IsEnabled, "manual code recreation");
        var recreated = Control<TextBox>("ReceiveCode").Text;
        Check(!string.IsNullOrEmpty(recreated) && recreated != originalCode, "manual recreation retained old code");
        Click("ChangeSendCodeButton");
        Check(Control<TextBlock>("AppMessage").Text == Localization.Current.Text("ReplaceSenderCode"), "sender change entry missing");
        Control<TextBox>("SendCode").Text = recreated;
        window.Close();
        var reopened = new MainWindow(Path.Combine(Root, "ui-connections", "connections.json"));
        reopened.Show();
        try
        {
            Check(reopened.FindControl<TextBox>("SendCode")!.Text == recreated, "sender code did not survive window recreation");
            Check(reopened.FindControl<TextBox>("ReceiveCode")!.Text == recreated, "receiver code did not survive window recreation");
            Check(reopened.FindControl<TextBox>("ReceiveDirectory")!.Text == destination, "destination did not survive restart");
            Check(reopened.FindControl<Button>("ReceiveButton")!.IsEnabled && !reopened.FindControl<Button>("StopReceiveButton")!.IsEnabled,
                "restoring a code silently started a listener");
            reopened.FindControl<Button>("ReceiveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => reopened.FindControl<Button>("StopReceiveButton")!.IsEnabled, "restored receiver startup");
            Check(reopened.FindControl<TextBox>("ReceiveCode")!.Text == recreated, "starting restored receiver changed code");
            reopened.FindControl<Button>("StopReceiveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => reopened.FindControl<Button>("ReceiveButton")!.IsEnabled, "restored receiver shutdown");
        }
        finally { reopened.Close(); }
        Console.WriteLine("PASS UI manual recreate/change / full window restart retains both codes and destination / listener stays off until started");

        var reverseHost = new MainWindow(Path.Combine(Root, "reverse-host", "connections.json"));
        var peerSettings = Path.Combine(Root, "reverse-peer", "connections.json");
        var reversePeer = new MainWindow(peerSettings);
        reverseHost.Show();
        reversePeer.Show();
        T Host<T>(string name) where T : Control => reverseHost.FindControl<T>(name)!;
        T Peer<T>(string name) where T : Control => reversePeer.FindControl<T>(name)!;
        void HostClick(string name) => Host<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void PeerClick(string name) => Peer<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            Host<TextBox>("ReceiveAddress").Text = "127.0.0.1";
            using (var listener = new TcpListener(IPAddress.Loopback, 0))
            {
                listener.Start();
                Host<TextBox>("ReceivePort").Text = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
            }
            Host<TextBox>("ReceiveDirectory").Text = Path.Combine(Root, "reverse-host-received");
            HostClick("ReceiveButton");
            await Until(() => Host<Button>("StopReceiveButton").IsEnabled, "reverse host listener startup");
            await using (var file = File.Create(source)) file.SetLength(Wire.ChunkSize * 10L);
            Host<ComboBox>("StreamPicker").SelectedIndex = 9;
            await reverseHost.AddPathsAsync([source]);
            HostClick("ReverseSendButton");
            await Until(() => Host<TextBlock>("SendStatus").Text == Localization.Current.Text("Core.WaitingForPeer"),
                "reverse source offer");
            Check(Host<ItemsControl>("FileList").Items.Cast<FileRow>().All(row => row.Status == Localization.Current.Text("Core.WaitingForPeer")),
                "ready reverse offer still shows calculating checksum in its file rows");
            Check(Host<TextBlock>("SendDetail").Text == Localization.Current.Text("ReverseOfferReadyDetail"),
                "ready reverse offer did not explain that hashing is complete and the peer must connect");
            Check(!Host<Button>("StopReceiveButton").IsEnabled && Host<Button>("PauseButton").IsEnabled,
                "reverse source can stop its listener without cancelling the offer");
            var hostCode = Host<TextBox>("ReceiveCode").Text;
            Peer<CheckBox>("ReverseReceiveMode").IsChecked = true;
            Check(!Peer<Grid>("LocalReceiveEndpoint").IsVisible && Peer<StackPanel>("ReverseReceiveEndpoint").IsVisible
                && !Peer<StackPanel>("LocalInvitationPanel").IsVisible, "reverse receive mode did not show peer-code controls");
            Peer<TextBox>("ReverseReceiveCode").Text = hostCode;
            Peer<TextBox>("ReceiveAddress").Text = "not-a-local-listener";
            Peer<TextBox>("ReceivePort").Text = "unused";
            var reverseDestination = Path.Combine(Root, "reverse-ui-received");
            Peer<TextBox>("ReceiveDirectory").Text = reverseDestination;
            PeerClick("ReceiveButton");
            Check(!Peer<CheckBox>("ReverseReceiveMode").IsEnabled && Peer<Button>("StopReceiveButton").IsEnabled,
                "reverse receiving did not lock conflicting controls or expose pause");
            PeerClick("StopReceiveButton");
            await Until(() => Peer<Button>("ReceiveButton").IsEnabled, "reverse receive pause");
            Check(Peer<TextBlock>("AppMessage").Text == Localization.Current.Text("ReverseReceivePaused"),
                "reverse receive pause did not show resume guidance");
            PeerClick("ReceiveButton");
            await Until(() => Peer<ProgressBar>("ReceiveProgress").Value > 0, "reverse progress before source pause");
            Check(Host<TextBlock>("SendEstimate").Text != Localization.Current.Text("TimeWaiting"), "active sender ETA missing");
            var sendStamp = typeof(MainWindow).GetField("lastSendUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var renderSend = typeof(MainWindow).GetMethod("RenderSend", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var originalStamp = sendStamp.GetValue(reverseHost);
            sendStamp.SetValue(reverseHost, Environment.TickCount64 - 6000);
            renderSend.Invoke(reverseHost, null);
            Check(Host<TextBlock>("SendEstimate").Text?.StartsWith(Localization.Current.Text("SendTimeRemainingRecent", "")) == true
                && Host<TextBlock>("SendDetail").Text?.Contains(Localization.Current.Text("SendRecentSpeed", "")) == true,
                "sparse sender progress lost ETA instead of showing a recent estimate");
            sendStamp.SetValue(reverseHost, Environment.TickCount64 - 300000);
            renderSend.Invoke(reverseHost, null);
            Check(Host<TextBlock>("SendEstimate").Text == Localization.Current.Text("TimeWaiting"), "expired sender estimate did not clear");
            sendStamp.SetValue(reverseHost, originalStamp);
            HostClick("PauseButton");
            await Until(() => Host<Button>("ReverseSendButton").IsEnabled
                && Peer<TextBlock>("ReceiveState").Text == Localization.Current.Text("Core.WaitingForSender"),
                "peer automatic wait after sender pause");
            Check(!Peer<Button>("ReceiveButton").IsEnabled && Peer<Button>("StopReceiveButton").IsEnabled,
                "sender pause stopped the receiver or disabled cancelling its automatic wait");
            Check(Host<TextBlock>("SendEstimate").Text == Localization.Current.Text("SendTimePaused"), "paused sender retained ETA");
            HostClick("ReverseSendButton");
            await Until(() => Peer<Button>("ReceiveButton").IsEnabled && Peer<ProgressBar>("ReceiveProgress").Value == 100,
                "reverse UI download completion");
            await Until(() => Host<Button>("SendButton").IsEnabled, "reverse source verified completion");
            Check(Peer<TextBlock>("ReceiveEstimate").Text?.Contains(TransferDisplay.ParallelMode(10, Localization.Current)) == true,
                "reverse UI receiver ignored sender-selected ten streams");
            Check(Host<TextBlock>("SendEstimate").Text == Localization.Current.Text("SendTimeComplete"), "sender verified completion ETA missing");
            Check(await Wire.FileHashAsync(Path.Combine(reverseDestination, "中文.bin"), default) == await Wire.FileHashAsync(source, default),
                "reverse UI transfer checksum mismatch");
            Check(new ConnectionStore(peerSettings).Load().Identity is null, "reverse peer created a local listener identity");
            reversePeer.Close();
            var reverseReopened = new MainWindow(peerSettings);
            try
            {
                Check(reverseReopened.FindControl<TextBox>("ReverseReceiveCode")!.Text == hostCode,
                    "reverse peer code was not retained across restart");
            }
            finally { reverseReopened.Close(); }
            HostClick("StopReceiveButton");
            await Until(() => Host<Button>("ReceiveButton").IsEnabled, "reverse host shutdown");
        }
        finally { reverseHost.Close(); reversePeer.Close(); }
        Console.WriteLine("PASS UI reverse send / outgoing-only receive / pause and resume / checksum / peer-code persistence / controls restored");

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
        var window = new MainWindow(Path.Combine(UiChecks.Root, "ui-connections", "connections.json"));
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

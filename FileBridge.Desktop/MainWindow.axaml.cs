using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FileBridge.Core;

namespace FileBridge.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<FileRow> rows = [];
    private readonly List<string> selected = [];
    private CancellationTokenSource? sending;
    private TransferReceiver? receiver;
    private bool busy;
    private long lastSendUpdate;
    private long lastReceiveUpdate;
    private readonly Localization language = Localization.Current;
    private readonly string detectedAddress;
    private string sendAction = "StartSending", sendState = "WaitingForFiles";
    private string receiveState = "NotStarted", receiveStatus = "WaitingToReceive";
    private TransferProgress? sendProgress, receiveProgress;
    private string messageKey = "Ready";
    private string? errorText, errorPrefix;
    private bool messageIsError;
    private readonly DispatcherTimer receiveTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MainWindow() : this(ConnectionStore.DefaultPath) { }

    internal MainWindow(string settingsFile)
    {
        connections = new ConnectionStore(settingsFile);
        InitializeComponent();
        FileList.ItemsSource = rows;
        StreamPicker.ItemsSource = Enumerable.Range(1, Wire.MaxParallelChunks).ToArray();
        StreamPicker.SelectedIndex = Wire.DefaultParallelChunks - 1;
        detectedAddress = NetworkAddressDiscovery.DiscoverAddress();
        ReceiveAddress.Text = detectedAddress;
        // Keep the existing folder name in every language so saved partial files remain discoverable.
        ReceiveDirectory.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FileBridge 接收");
        InitializeConnections();
        ApplyLanguage();
        receiveTimer.Tick += (_, _) => { if (receiveProgress?.Stage == TransferStage.Receiving) RenderReceive(); };
        Closed += (_, _) => { receiveTimer.Stop(); sending?.Cancel(); reverseReceiving?.Cancel(); _ = CloseReceiverAsync(); };
    }
    private string FormatBytes(long value) => TransferDisplay.Bytes(value, language.Culture);

    private void ChangeLanguage(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string selectedLanguage } || selectedLanguage == language.Language) return;
        var saved = language.SetLanguage(selectedLanguage);
        ApplyLanguage();
        if (!saved) Message("LanguageNotSaved", true);
    }

    private void ApplyLanguage()
    {
        ChineseLanguageItem.IsChecked = language.Language == "zh";
        EnglishLanguageItem.IsChecked = language.Language == "en";
        foreach (var key in language.Keys) Resources["Text." + key] = language.Text(key);
        NetworkLabel.Text = language.Text(detectedAddress == "127.0.0.1" ? "LocalOnly" : "LocalAddress", detectedAddress);
        VersionLabel.Text = language.Text("Version", typeof(MainWindow).Assembly.GetName().Version!.ToString(3));
        RenderSelection();
        SendButton.Content = language.Text(sendAction);
        RenderSend();
        RenderReceive();
        RenderMessage();
        SetReceiverControls();
        foreach (var row in rows) row.RefreshLanguage();
    }

    private void RenderSelection() => SelectionLabel.Text = rows.Count == 0 ? language.Text("SelectionHint")
        : language.Text("SelectedFiles", rows.Count, FormatBytes(rows.Sum(r => r.File.Size)));

    private void RenderSend()
    {
        if (sendProgress is not { } p)
        {
            SendProgress.Value = 0;
            SendPercent.Text = "0%";
            SendStatus.Text = language.Text(sendState);
            SendDetail.Text = language.Text("ParallelHint");
            return;
        }
        var percent = p.Total > 0 ? 100.0 * p.Completed / p.Total : 0;
        SendStatus.Text = TransferDisplay.StageText(p, language) + " · " + p.FileName;
        SendProgress.Value = percent;
        SendPercent.Text = percent.ToString("0.0", language.Culture) + "%";
        SendDetail.Text = $"{FormatBytes(p.Completed)} / {FormatBytes(p.Total)}"
            + (p.BytesPerSecond > 0 ? $" · {FormatBytes((long)p.BytesPerSecond)}/s" : "")
            + " · " + TransferDisplay.ParallelMode(p.ParallelChunks, language);
    }

    private void RenderReceive()
    {
        ReceiveState.Text = language.Text(receiveState);
        if (receiveProgress is not { } p)
        {
            ReceiveProgress.Value = 0;
            ReceiveStatus.Text = language.Text(receiveStatus);
            ReceiveDetail.Text = language.Text("ResumeHint");
            ReceiveEstimate.Text = language.Text("ReceiveMetricsHint");
            return;
        }
        ReceiveStatus.Text = TransferDisplay.StageText(p, language) + (p.FileName.Length > 0 ? " · " + p.FileName : "");
        ReceiveProgress.Value = p.Total > 0 ? 100.0 * p.Completed / p.Total : 0;
        var active = (receiver is not null || reverseReceiving is not null) && p.Stage == TransferStage.Receiving;
        var speed = TransferDisplay.ReceiveSpeed(p, active, Environment.TickCount64 - lastReceiveUpdate);
        ReceiveDetail.Text = $"{FormatBytes(p.Completed)} / {FormatBytes(p.Total)} · " + language.Text("ReceiveSpeed", FormatBytes((long)speed) + "/s");
        ReceiveEstimate.Text = TransferDisplay.ReceiveEstimate(p, speed, language) + " · " + TransferDisplay.ParallelMode(p.ParallelChunks, language);
    }

    // Progress<T> is created on the UI thread and dispatches these callbacks there.
    private void UpdateSendProgress(TransferProgress progress)
    {
        var now = Environment.TickCount64;
        if (progress.Stage == TransferStage.Sending && now - lastSendUpdate < 100) return;
        lastSendUpdate = now;
        var row = rows.FirstOrDefault(r => r.Name == progress.FileName);
        if (row is not null) row.Progress = progress;
        sendProgress = progress;
        RenderSend();
    }

    private void UpdateReceiveProgress(TransferProgress progress)
    {
        var now = Environment.TickCount64;
        if (progress.Stage == TransferStage.Receiving && now - lastReceiveUpdate < 100) return;
        lastReceiveUpdate = now;
        receiveProgress = progress;
        RenderReceive();
    }

    private void SetSendAction(string key) { sendAction = key; SendButton.Content = language.Text(key); }
    private void SetSendState(string key) { sendState = key; sendProgress = null; RenderSend(); }

    private void Message(string key, bool error = false)
    {
        messageKey = key;
        errorText = null;
        errorPrefix = null;
        messageIsError = error;
        RenderMessage();
    }

    private void ShowError(Exception error, string? prefix = null)
    {
        errorText = error.Message;
        errorPrefix = prefix;
        messageIsError = true;
        RenderMessage();
    }

    private void RenderMessage()
    {
        AppMessage.Text = errorText is null ? language.Text(messageKey)
            : errorPrefix is null ? language.CoreText(errorText) : language.Text(errorPrefix, language.CoreText(errorText));
        AppMessage.Foreground = Avalonia.Media.Brush.Parse(messageIsError ? "#B43D37" : "#66768A");
    }
    private async void ChooseFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = language.Text("PickerFilesTitle"), AllowMultiple = true });
            await AddPathsAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void ChooseFolder(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = language.Text("PickerSourceFolderTitle"), AllowMultiple = false });
            await AddPathsAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
        }
        catch (Exception error) { ShowError(error); }
    }
    internal async Task AddPathsAsync(IEnumerable<string> paths)
    {
        if (busy) return;
        var additions = paths.ToArray();
        if (additions.Length == 0) return;
        var next = selected.Concat(additions).Distinct(StringComparer.Ordinal).ToList();
        busy = true;
        SetSendControls();
        Message("ScanningFiles");
        try
        {
            var files = await Task.Run(() => TransferSender.Collect(next));
            selected.Clear();
            selected.AddRange(next);
            rows.Clear();
            foreach (var file in files) rows.Add(new FileRow(file));
            RenderSelection();
            SetSendState("FilesReady");
            Message("FilesAdded");
        }
        finally
        {
            busy = false;
            SetSendControls();
        }
    }
    private void ClearFiles(object? sender, RoutedEventArgs e)
    {
        selected.Clear();
        rows.Clear();
        RenderSelection();
        SetSendState("WaitingForFiles");
        SetSendAction("StartSending");
    }
    private async void SendFiles(object? sender, RoutedEventArgs e) => await SendSelectedAsync(false);
    private async void SendReverseFiles(object? sender, RoutedEventArgs e) => await SendSelectedAsync(true);

    private async Task SendSelectedAsync(bool reverse)
    {
        if (busy) return;
        try
        {
            if (rows.Count == 0) throw new InvalidOperationException("请先选择文件或文件夹。");
            var code = SendCode.Text ?? "";
            if (reverse && receiver is null) throw new InvalidOperationException("请先在接收页启动本机接收服务，再反向发送。");
            if (!reverse) _ = Invitation.Parse(code);
            reverseSending = reverse;
            busy = true;
            sending = new CancellationTokenSource();
            SetSendControls();
            SetReceiverControls();
            Message(reverse ? "ReverseSendActive" : "TransferActive");
            var progress = new Progress<TransferProgress>(UpdateSendProgress);
            var files = rows.Select(r => r.File).ToList();
            var streams = StreamPicker.SelectedIndex + 1;
            if (reverse) await Task.Run(() => receiver!.SendReverseAsync(files, progress, sending.Token, streams));
            else await Task.Run(() => TransferSender.SendAsync(files, code, progress, sending.Token, streams));
            Message("SendComplete");
            SetSendAction("VerifyAgain");
        }
        catch (OperationCanceledException)
        {
            Message(reverse ? "ReverseSendPaused" : "Paused");
            SetSendAction(reverse ? "StartSending" : "ContinueSending");
        }
        catch (Exception error)
        {
            ShowError(error);
            SetSendState("SendFailed");
            SetSendAction(reverse ? "StartSending" : "RetryResume");
        }
        finally
        {
            busy = false;
            reverseSending = false;
            sending?.Dispose();
            sending = null;
            SetSendControls();
            SetReceiverControls();
        }
    }
    private void SetSendControls()
    {
        SendButton.IsEnabled = !busy;
        ReverseSendButton.IsEnabled = !busy;
        ChangeSendCodeButton.IsEnabled = !busy;
        PauseButton.IsEnabled = sending is not null;
        FilesButton.IsEnabled = !busy;
        FolderButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        SendCode.IsReadOnly = busy;
        StreamPicker.IsEnabled = !busy;
    }
    private void PauseSending(object? sender, RoutedEventArgs e) { sending?.Cancel(); PauseButton.IsEnabled = false; }
    private async void ChooseDestination(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = language.Text("PickerDestinationTitle") });
            if (folders.Count > 0) ReceiveDirectory.Text = folders[0].TryGetLocalPath();
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void OpenReceiveDirectory(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = ReceiveDirectory.Text?.Trim() ?? "";
            if (path.Length == 0) { Message("Core.ChooseSaveLocation", true); return; }
            var directory = new DirectoryInfo(path);
            if (!directory.Exists) { Message("ReceiveDirectoryMissing", true); return; }
            if (!await Launcher.LaunchDirectoryInfoAsync(directory)) { Message("OpenReceiveDirectoryFailed", true); return; }
            Message("ReceiveDirectoryOpened");
        }
        catch (Exception error) { ShowError(error); }
    }
    private async void StartReceiving(object? sender, RoutedEventArgs e)
    {
        if (ReverseReceiveMode.IsChecked == true) { await ReceiveReverseAsync(); return; }
        if (receiver is not null || receiverChanging) return;
        receiverChanging = true;
        SetReceiverControls();
        try
        {
            var (address, port) = ReadReceiveEndpoint();
            var directory = ReceiveDirectory.Text?.Trim() ?? "";
            if (directory.Length == 0) throw new InvalidOperationException("请选择保存位置。");
            if (!SaveConnectionPreferences()) return;
            receiverLease = connections.AcquireReceiverLease();
            var oldIdentity = storedIdentity;
            storedIdentity = await Task.Run(() => connections.GetIdentity());
            var identity = storedIdentity;
            var progress = new Progress<TransferProgress>(UpdateReceiveProgress);
            receiver = await Task.Run(() => TransferReceiver.StartAsync(address, port, directory, progress, identity: identity));
            ReceiveCode.Text = receiver.Invitation.Encode();
            receiveState = "ReceiverStarted";
            receiveStatus = "WaitingForSender";
            receiveProgress = null;
            RenderReceive();
            CopyButton.IsEnabled = true;
            receiveTimer.Start();
            Message(oldIdentity is not null && oldIdentity.Token != identity.Token ? "InvitationUpdated" : "ShareInvitation");
        }
        catch (Exception error) { ShowError(error, "ReceiveStartFailed"); }
        finally
        {
            if (receiver is null) { receiverLease?.Dispose(); receiverLease = null; }
            receiverChanging = false;
            SetReceiverControls();
        }
    }
    private async void StopReceiving(object? sender, RoutedEventArgs e)
    {
        if (reverseReceiving is not null) { reverseReceiving.Cancel(); StopReceiveButton.IsEnabled = false; return; }
        if (receiverChanging) return;
        receiverChanging = true;
        SetReceiverControls();
        try
        {
            if (receiver is not null) await receiver.DisposeAsync();
            Message("ReceiveStopped");
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            receiveTimer.Stop();
            receiver = null;
            receiverLease?.Dispose();
            receiverLease = null;
            receiverChanging = false;
            SetReceiverControls();
            receiveState = "ReceiverStoppedState";
            RenderReceive();
        }
    }
    private void SetReceiverFields(bool enabled)
    {
        ReceiveAddress.IsEnabled = enabled;
        ReceivePort.IsEnabled = enabled;
        ReceiveDirectory.IsEnabled = enabled;
        DestinationButton.IsEnabled = enabled;
    }
    private async void CopyCode(object? sender, RoutedEventArgs e)
    {
        try { if (Clipboard is not null) { await Clipboard.SetTextAsync(ReceiveCode.Text); Message("InvitationCopied"); } }
        catch (Exception error) { ShowError(error); }
    }
}

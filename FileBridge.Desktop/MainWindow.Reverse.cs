using Avalonia.Interactivity;
using FileBridge.Core;

namespace FileBridge.Desktop;

public sealed partial class MainWindow
{
    private bool reverseSending;
    private CancellationTokenSource? reverseReceiving;
    private Task? reverseDownload;

    private void ReverseReceiveModeChanged(object? sender, RoutedEventArgs e)
    {
        if (ReceiveButton is not null) SetReceiverControls();
    }

    private async Task ReceiveReverseAsync()
    {
        if (receiver is not null || reverseReceiving is not null || receiverChanging) return;
        reverseReceiving = new CancellationTokenSource();
        SetReceiverControls();
        try
        {
            var code = ReverseReceiveCode.Text ?? "";
            _ = Invitation.Parse(code);
            var directory = ReceiveDirectory.Text?.Trim() ?? "";
            if (directory.Length == 0) throw new InvalidOperationException("请选择保存位置。");
            if (!SaveConnectionPreferences()) return;
            receiverLease = connections.AcquireReceiverLease();
            receiveProgress = null;
            receiveState = "ReverseConnecting";
            receiveStatus = "ReverseConnecting";
            RenderReceive();
            Message("ReverseReceiving");
            receiveTimer.Start();
            var progress = new Progress<TransferProgress>(UpdateReceiveProgress);
            reverseDownload = Task.Run(() => ReverseTransfer.ReceiveAsync(code, directory, progress, reverseReceiving.Token));
            await reverseDownload;
            Message("ReverseReceiveComplete");
        }
        catch (OperationCanceledException) { Message("ReverseReceivePaused"); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            receiveTimer.Stop();
            reverseDownload = null;
            reverseReceiving.Dispose();
            reverseReceiving = null;
            receiverLease?.Dispose();
            receiverLease = null;
            receiveState = "ReceiverStoppedState";
            RenderReceive();
            SetReceiverControls();
        }
    }
}

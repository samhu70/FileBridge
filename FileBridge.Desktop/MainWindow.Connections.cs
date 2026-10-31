using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FileBridge.Core;

namespace FileBridge.Desktop;

public sealed partial class MainWindow
{
    private readonly ConnectionStore connections;
    private readonly DispatcherTimer saveConnectionsTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ReceiverIdentity? storedIdentity;
    private FileStream? receiverLease;
    private bool receiverChanging;
    private bool invitationPreviewPending;

    private void InitializeConnections()
    {
        try
        {
            var saved = connections.Load();
            SendCode.Text = saved.SendCode ?? "";
            if (!string.IsNullOrEmpty(saved.Address)) ReceiveAddress.Text = saved.Address;
            ReceivePort.Text = saved.Port ?? "8787";
            if (!string.IsNullOrEmpty(saved.Directory)) ReceiveDirectory.Text = saved.Directory;
            storedIdentity = saved.Identity;
            RefreshInvitationPreview();
            if (ReceiveCode.Text?.Length > 0 || SendCode.Text.Length > 0) Message("ConnectionsRestored");
        }
        catch (Exception error) { ShowError(error); }

        saveConnectionsTimer.Tick += (_, _) =>
        {
            saveConnectionsTimer.Stop();
            SaveConnectionPreferences();
            if (invitationPreviewPending && receiver is null && !receiverChanging) RefreshInvitationPreview();
        };
        foreach (var field in new[] { SendCode, ReceiveAddress, ReceivePort, ReceiveDirectory })
            field.TextChanged += (_, _) =>
            {
                if (field == ReceiveAddress || field == ReceivePort)
                {
                    invitationPreviewPending = true;
                    ReceiveCode.Text = "";
                    CopyButton.IsEnabled = false;
                }
                saveConnectionsTimer.Stop();
                saveConnectionsTimer.Start();
            };
        Closing += (_, _) => { saveConnectionsTimer.Stop(); SaveConnectionPreferences(); };
    }

    private bool SaveConnectionPreferences()
    {
        saveConnectionsTimer.Stop();
        try
        {
            connections.SavePreferences(new(SendCode.Text ?? "", ReceiveAddress.Text?.Trim() ?? "",
                ReceivePort.Text ?? "", ReceiveDirectory.Text?.Trim() ?? ""));
            return true;
        }
        catch (Exception error) { ShowError(error, "ConnectionSaveFailed"); return false; }
    }

    private void RefreshInvitationPreview()
    {
        invitationPreviewPending = false;
        ReceiveCode.Text = "";
        CopyButton.IsEnabled = false;
        if (storedIdentity is null) return;
        try
        {
            if (!int.TryParse(ReceivePort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return;
            ReceiveCode.Text = storedIdentity.InvitationFor(ReceiveAddress.Text?.Trim() ?? "", port).Encode();
            CopyButton.IsEnabled = true;
        }
        catch (ArgumentException) { }
        catch (Exception error) when (error is InvalidDataException or CryptographicException) { Message("InvitationRenewalNeeded", true); }
    }

    private void SetReceiverControls()
    {
        var active = receiver is not null;
        ReceiveButton.IsEnabled = !active && !receiverChanging;
        StopReceiveButton.IsEnabled = active && !receiverChanging;
        RecreateCodeButton.IsEnabled = !active && !receiverChanging;
        CopyButton.IsEnabled = !receiverChanging && !string.IsNullOrEmpty(ReceiveCode.Text);
        SetReceiverFields(!active && !receiverChanging);
    }

    private (string Address, int Port) ReadReceiveEndpoint()
    {
        if (!int.TryParse(ReceivePort.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("端口应在 1–65535 之间。");
        if (!IPAddress.TryParse(ReceiveAddress.Text?.Trim(), out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException("请填写接收电脑的具体 IP 和有效端口。");
        return (ip.ToString(), port);
    }

    private void ChangeSendCode(object? sender, RoutedEventArgs e)
    {
        if (busy) return;
        SendCode.Focus();
        SendCode.SelectAll();
        Message("ReplaceSenderCode");
    }

    private async void RecreateReceiverCode(object? sender, RoutedEventArgs e)
    {
        if (receiver is not null || receiverChanging) return;
        receiverChanging = true;
        SetReceiverControls();
        try
        {
            _ = ReadReceiveEndpoint();
            using var lease = connections.AcquireReceiverLease();
            storedIdentity = await Task.Run(() => connections.GetIdentity(recreate: true));
            if (!SaveConnectionPreferences()) return;
            RefreshInvitationPreview();
            Message("ReceiverCodeRecreated");
        }
        catch (Exception error) { ShowError(error); }
        finally { receiverChanging = false; SetReceiverControls(); }
    }

    private async Task CloseReceiverAsync()
    {
        try { if (receiver is not null) await receiver.DisposeAsync(); }
        finally { receiverLease?.Dispose(); receiverLease = null; }
    }
}

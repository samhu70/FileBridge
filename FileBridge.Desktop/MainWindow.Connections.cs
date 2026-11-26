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
    private readonly FileStream instanceLease;
    private bool receiverChanging;
    private bool invitationPreviewPending;
    private TailscaleFunnel? funnel;
    private string? storedPublicUrl;
    private Uri? funnelSetupUri;

    private void InitializeConnections()
    {
        try
        {
            var saved = connections.Load();
            SendCode.Text = saved.SendCode ?? "";
            ReverseReceiveCode.Text = saved.ReverseCode ?? "";
            if (!string.IsNullOrEmpty(saved.Address)) ReceiveAddress.Text = saved.Address;
            ReceivePort.Text = saved.Port ?? "8787";
            if (!string.IsNullOrEmpty(saved.Directory)) ReceiveDirectory.Text = saved.Directory;
            storedIdentity = saved.Identity;
            storedPublicUrl = saved.PublicUrl;
            PublicReceiveEnabled.IsChecked = saved.PublicEnabled;
            RefreshInvitationPreview();
            if ((ReceiveCode.Text?.Length > 0 || SendCode.Text.Length > 0) && storedIdentity?.IsUsable != false) Message("ConnectionsRestored");
        }
        catch (Exception error) { ShowError(error); }

        TransferTabs.SelectionChanged += async (_, e) =>
        {
            if (e.Source == TransferTabs && TransferTabs.SelectedIndex == 1 && storedIdentity is null
                && ReverseReceiveMode.IsChecked != true)
                await CreateReceiverCodeAsync(recreate: false);
        };

        saveConnectionsTimer.Tick += (_, _) =>
        {
            saveConnectionsTimer.Stop();
            SaveConnectionPreferences();
            if (invitationPreviewPending && receiver is null && !receiverChanging) RefreshInvitationPreview();
        };
        foreach (var field in new[] { SendCode, ReceiveAddress, ReceivePort, ReceiveDirectory, ReverseReceiveCode })
            field.TextChanged += (_, _) =>
            {
                if (receiver is null && (field == ReceiveAddress || field == ReceivePort))
                {
                    invitationPreviewPending = true;
                    ReceiveCode.Text = "";
                    CopyButton.IsEnabled = false;
                }
                saveConnectionsTimer.Stop();
                saveConnectionsTimer.Start();
            };
        Closing += (_, _) => { saveConnectionsTimer.Stop(); SaveConnectionPreferences(); };
        PublicReceiveEnabled.IsCheckedChanged += (_, _) =>
        {
            funnelSetupUri = null;
            SetReceiverControls();
            SaveConnectionPreferences();
        };
    }

    private bool SaveConnectionPreferences()
    {
        saveConnectionsTimer.Stop();
        try
        {
            connections.SavePreferences(new(SendCode.Text ?? "", ReceiveAddress.Text?.Trim() ?? "",
                ReceivePort.Text ?? "", ReceiveDirectory.Text?.Trim() ?? "", ReverseCode: ReverseReceiveCode.Text ?? "",
                PublicEnabled: PublicReceiveEnabled.IsChecked == true, PublicUrl: storedPublicUrl));
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
            ReceiveCode.Text = (storedIdentity.InvitationFor(ReceiveAddress.Text?.Trim() ?? "", port) with { PublicUrl = storedPublicUrl }).Encode();
            CopyButton.IsEnabled = true;
        }
        catch (ArgumentException) { }
        catch (Exception error) when (error is InvalidDataException or CryptographicException) { Message("InvitationRenewalNeeded", true); }
    }

    private void SetReceiverControls()
    {
        var active = receiver is not null;
        var pulling = reverseReceiving is not null;
        var reverse = ReverseReceiveMode.IsChecked == true;
        ReceiveButton.IsEnabled = !active && !pulling && !receiverChanging;
        StopReceiveButton.IsEnabled = (active || pulling) && !receiverChanging && !reverseSending;
        ReceiveButton.Content = language.Text(reverse ? "ConnectAndReceive" : "StartReceiving");
        StopReceiveButton.Content = language.Text(reverse ? "PauseReceiving" : "StopReceiving");
        ReverseReceiveMode.IsEnabled = !active && !pulling && !receiverChanging;
        RecreateCodeButton.IsEnabled = !active && !pulling && !receiverChanging && !reverse;
        CopyButton.IsEnabled = !receiverChanging && !string.IsNullOrEmpty(ReceiveCode.Text);
        SetReceiverFields(!active && !pulling && !receiverChanging);
        ReverseReceiveCode.IsReadOnly = active || pulling || receiverChanging;
        LocalReceiveEndpoint.IsVisible = !reverse;
        ReverseReceiveEndpoint.IsVisible = reverse;
        LocalInvitationPanel.IsVisible = !reverse;
        PublicReceiveEnabled.IsVisible = !reverse;
        PublicReceiveEnabled.IsEnabled = !active && !pulling && !receiverChanging;
        PublicReceiveHint.IsVisible = !reverse && PublicReceiveEnabled.IsChecked == true;
        FunnelSetupButton.IsVisible = !reverse && PublicReceiveEnabled.IsChecked == true && funnelSetupUri is not null;
        FunnelSetupButton.NavigateUri = funnelSetupUri;
        PublicEndpoint.IsVisible = storedPublicUrl is not null;
        PublicEndpoint.Text = storedPublicUrl is null ? "" : language.Text("PublicEndpoint", storedPublicUrl);
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
        => await CreateReceiverCodeAsync(recreate: true);

    private async Task CreateReceiverCodeAsync(bool recreate)
    {
        if (receiver is not null || reverseReceiving is not null || receiverChanging) return;
        receiverChanging = true;
        SetReceiverControls();
        try
        {
            _ = ReadReceiveEndpoint();
            storedIdentity = await Task.Run(() => connections.GetIdentity(recreate));
            if (!SaveConnectionPreferences()) return;
            RefreshInvitationPreview();
            if (recreate) Message("ReceiverCodeRecreated");
        }
        catch (Exception error) { ShowError(error); }
        finally { receiverChanging = false; SetReceiverControls(); }
    }

    private async Task CloseReceiverAsync()
    {
        try
        {
            while (receiverChanging) await Task.Delay(20);
            if (reverseDownload is not null) { try { await reverseDownload; } catch { } }
            await StopLocalReceiverAsync();
        }
        finally { instanceLease.Dispose(); }
    }

    private async Task StopLocalReceiverAsync()
    {
        try { if (funnel is not null) await funnel.DisposeAsync(); }
        finally
        {
            funnel = null;
            if (receiver is not null) await receiver.DisposeAsync();
        }
    }
}

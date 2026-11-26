namespace FileBridge.Core;

/// <summary>Keep a pinned connection, preferring direct access and selecting again after an interruption.</summary>
internal sealed class PeerConnection(Invitation invitation) : IDisposable
{
    private HttpClient? client;

    public async Task<HttpClient> GetAsync(CancellationToken ct)
    {
        if (client is not null) return client;
        if (invitation.PublicUrl is null) return client = TransferSender.CreateClient(invitation);
        Exception? failure = null;
        foreach (var url in new[] { invitation.Url, invitation.PublicUrl }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = TransferSender.CreateClient(invitation with { Url = url });
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(url.Equals(invitation.Url, StringComparison.OrdinalIgnoreCase) ? 3 : 15));
                await TransferSender.RequestAsync(candidate, "v2/ping", null, timeout.Token);
                return client = candidate;
            }
            catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or OperationCanceledException or TransferException)
            {
                failure = error;
                candidate.Dispose();
            }
            catch { candidate.Dispose(); throw; }
        }
        if (failure is OperationCanceledException) throw new HttpRequestException("连接端点超时。", failure);
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure!).Throw();
        throw new InvalidOperationException();
    }

    public void Reset() { client?.Dispose(); client = null; }
    public void Dispose() => Reset();
}

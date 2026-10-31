using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FileBridge.Core;

/// <summary>Owns TLS certificates through in-flight handshakes and renews without rotating identity.</summary>
internal sealed class ReceiverCertificateCache : IDisposable
{
    private readonly object gate = new();
    private readonly ReceiverIdentity identity;
    private readonly bool publicKeyPin;
    private readonly List<X509Certificate2> retired = [];
    private X509Certificate2 current;
    private bool disposed;

    public ReceiverCertificateCache(ReceiverIdentity source)
    {
        identity = source with { Pfx = source.Pfx?.ToArray() ?? [] };
        try { publicKeyPin = identity.PublicKeyPin; current = identity.LoadCertificate(); }
        catch { CryptographicOperations.ZeroMemory(identity.Pfx); throw; }
    }

    public X509Certificate2 GetCertificate() => GetCertificate(DateTimeOffset.UtcNow);

    internal X509Certificate2 GetCertificate(DateTimeOffset now)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (publicKeyPin && (now.UtcDateTime < current.NotBefore.ToUniversalTime()
                || now.AddDays(1).UtcDateTime >= current.NotAfter.ToUniversalTime()))
            {
                var replacement = identity.LoadCertificate(now);
                // A handshake may still hold the previous certificate. Release all of them at shutdown.
                retired.Add(current);
                current = replacement;
            }
            return current;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            current.Dispose();
            foreach (var certificate in retired) certificate.Dispose();
            CryptographicOperations.ZeroMemory(identity.Pfx);
        }
    }
}

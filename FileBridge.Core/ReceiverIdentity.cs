using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace FileBridge.Core;

/// <summary>Local receiver credentials. Never send the PFX to a peer or include it in logs.</summary>
public sealed record ReceiverIdentity(string Token, byte[] Pfx)
{
    public static ReceiverIdentity Create() => Create(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7));

    internal static ReceiverIdentity Create(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=FileBridge", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), certificate.Export(X509ContentType.Pfx));
    }

    [JsonIgnore]
    public bool IsUsable
    {
        get
        {
            try { using var certificate = LoadCertificate(); return true; }
            catch (Exception error) when (error is CryptographicException or InvalidDataException) { return false; }
        }
    }

    internal X509Certificate2 LoadCertificate()
    {
        if (!Wire.ValidHash(Token) || Pfx is not { Length: > 0 and <= 16384 })
            throw new InvalidDataException("保存的接收凭据无效，请重新创建连接码。");
        // Schannel needs an imported user key; macOS needs its temporary keychain, not EphemeralKeySet.
        var certificate = X509CertificateLoader.LoadPkcs12(Pfx, null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet);
        var now = DateTime.UtcNow;
        if (certificate.HasPrivateKey && now >= certificate.NotBefore.ToUniversalTime() && now < certificate.NotAfter.ToUniversalTime())
            return certificate;
        certificate.Dispose();
        throw new InvalidDataException("接收证书已失效，请重新创建连接码。");
    }

    /// <summary>Derive the same invitation while credentials, address and port remain unchanged.</summary>
    public Invitation InvitationFor(string address, int port)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || port is < 1 or > 65535)
            throw new ArgumentException("请填写接收电脑的具体 IP 和有效端口。");
        using var certificate = LoadCertificate();
        return new($"https://{new IPEndPoint(ip, port)}", Token, Wire.Hash(certificate.RawData));
    }
}

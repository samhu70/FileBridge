using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace FileBridge.Core;

/// <summary>Local receiver credentials. Never send the PFX to a peer or include it in logs.</summary>
public sealed record ReceiverIdentity(string Token, byte[] Pfx)
{
    // UUID-derived OID (ITU-T X.667); store the mode in the PFX so old preference writers cannot erase it.
    private const string PublicKeyPinOid = "2.25.113014805965853602799128767528523782477";

    [JsonIgnore]
    public bool PublicKeyPin
    {
        get { using var certificate = LoadStoredCertificate(); return UsesPublicKeyPin(certificate); }
    }

    private static bool UsesPublicKeyPin(X509Certificate2 certificate) => certificate.Extensions[PublicKeyPinOid] is not null;

    public static ReceiverIdentity Create() => Create(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(365));

    internal static ReceiverIdentity Create(DateTimeOffset notBefore, DateTimeOffset notAfter, bool publicKeyPin = true)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = CreateCertificate(key, notBefore, notAfter, publicKeyPin);
        return new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), certificate.Export(X509ContentType.Pfx));
    }

    private static X509Certificate2 CreateCertificate(ECDsa key, DateTimeOffset notBefore, DateTimeOffset notAfter, bool publicKeyPin)
    {
        var request = new CertificateRequest("CN=FileBridge", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        if (publicKeyPin) request.CertificateExtensions.Add(new X509Extension(PublicKeyPinOid, [0x05, 0x00], critical: false));
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    [JsonIgnore]
    public bool IsUsable
    {
        get
        {
            try { using var certificate = LoadStoredCertificate(); return true; }
            catch (Exception error) when (error is CryptographicException or InvalidDataException) { return false; }
        }
    }

    internal X509Certificate2 LoadStoredCertificate()
    {
        if (!Wire.ValidHash(Token) || Pfx is not { Length: > 0 and <= 16384 })
            throw new InvalidDataException("保存的接收凭据无效，请手动重新创建连接码。");
        // Exportability permits same-key reissuance on both OSes; Schannel also needs an imported user key.
        var certificate = X509CertificateLoader.LoadPkcs12(Pfx, null,
            X509KeyStorageFlags.Exportable | (OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet));
        if (certificate.HasPrivateKey) return certificate;
        certificate.Dispose();
        throw new InvalidDataException("保存的接收凭据无效，请手动重新创建连接码。");
    }

    internal X509Certificate2 LoadCertificate(DateTimeOffset? time = null)
    {
        var now = time ?? DateTimeOffset.UtcNow;
        var certificate = LoadStoredCertificate();
        var valid = now.UtcDateTime >= certificate.NotBefore.ToUniversalTime() && now.UtcDateTime < certificate.NotAfter.ToUniversalTime();
        if (!UsesPublicKeyPin(certificate))
        {
            if (valid) return certificate;
            certificate.Dispose();
            throw new InvalidDataException("旧版连接码的证书已过期，请手动重新创建长期连接码。");
        }
        if (valid && now.AddDays(1).UtcDateTime < certificate.NotAfter.ToUniversalTime()) return certificate;
        using (certificate)
        using (var key = certificate.GetECDsaPrivateKey() ?? throw new InvalidDataException("保存的接收凭据无效，请手动重新创建连接码。"))
        using (var renewed = CreateCertificate(key, now.AddMinutes(-1), now.AddDays(365), publicKeyPin: true))
        {
            // Reimport so Schannel receives an ordinary user key rather than a transient signer handle.
            var pfx = renewed.Export(X509ContentType.Pfx);
            try
            {
                return X509CertificateLoader.LoadPkcs12(pfx, null,
                    OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.DefaultKeySet);
            }
            finally { CryptographicOperations.ZeroMemory(pfx); }
        }
    }

    /// <summary>Derive a stable invitation; TLS renewal never changes a public-key-pinned code.</summary>
    public Invitation InvitationFor(string address, int port)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || port is < 1 or > 65535)
            throw new ArgumentException("请填写接收电脑的具体 IP 和有效端口。");
        using var certificate = LoadStoredCertificate();
        return new($"https://{new IPEndPoint(ip, port)}", Token, Wire.CertificatePin(certificate, UsesPublicKeyPin(certificate)), UsesPublicKeyPin(certificate));
    }
}

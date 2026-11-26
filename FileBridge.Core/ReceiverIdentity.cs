using System.Net;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace FileBridge.Core;

/// <summary>Local receiver credentials. Never send the PFX to a peer or include it in logs.</summary>
public sealed record ReceiverIdentity(string Token, byte[] Pfx)
{
    // Keep the mode inside the PFX so preference writers cannot erase it.
    private const string PublicKeyPinName = "FileBridge fb3";
    // Legacy identity marker; Windows cannot enumerate this UUID-sized OID.
    private const string PublicKeyPinOid = "2.25.113014805965853602799128767528523782477";

    [JsonIgnore]
    public bool PublicKeyPin
    {
        get { using var certificate = LoadStoredCertificate(); return UsesPublicKeyPin(certificate); }
    }

    private static bool UsesPublicKeyPin(X509Certificate2 certificate)
    {
        if (certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == PublicKeyPinName) return true;
        // Crypt32 cannot expose the UUID-sized OID through certificate.Extensions on Windows.
        try
        {
            var tbs = new AsnReader(certificate.RawDataMemory, AsnEncodingRules.DER).ReadSequence().ReadSequence();
            var extensionTag = new Asn1Tag(TagClass.ContextSpecific, 3);
            while (tbs.HasData)
            {
                if (tbs.PeekTag().HasSameClassAndValue(extensionTag))
                {
                    var extensions = tbs.ReadSequence(extensionTag).ReadSequence();
                    while (extensions.HasData)
                        if (extensions.ReadSequence().ReadObjectIdentifier() == PublicKeyPinOid) return true;
                    return false;
                }
                tbs.ReadEncodedValue();
            }
            return false;
        }
        catch (AsnContentException error)
        {
            throw new CryptographicException("保存的接收凭据无效，请手动重新创建连接码。", error);
        }
    }

    public static ReceiverIdentity Create() => Create(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(365));

    internal static ReceiverIdentity Create(DateTimeOffset notBefore, DateTimeOffset notAfter, bool publicKeyPin = true)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = CreateCertificate(key, notBefore, notAfter, publicKeyPin);
        return new(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), certificate.Export(X509ContentType.Pfx));
    }

    private static X509Certificate2 CreateCertificate(ECDsa key, DateTimeOffset notBefore, DateTimeOffset notAfter, bool publicKeyPin)
    {
        var request = new CertificateRequest(publicKeyPin ? $"CN={PublicKeyPinName}" : "CN=FileBridge", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
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
        // Keep the mode marker in the saved PFX; omit it from TLS so Windows can read every extension.
        using (certificate)
        using (var key = LoadSigningKey(certificate))
        using (var renewed = CreateCertificate(key, now.AddMinutes(-1), now.AddDays(365), publicKeyPin: false))
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

    private ECDsa LoadSigningKey(X509Certificate2 certificate)
    {
        if (certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == PublicKeyPinName)
            return certificate.GetECDsaPrivateKey() ?? throw new InvalidDataException("保存的接收凭据无效，请手动重新创建连接码。");

        // ECDsa certificate helpers enumerate extensions, so read legacy key bags directly.
        var info = Pkcs12Info.Decode(Pfx, out var consumed, skipCopy: true);
        if (consumed != Pfx.Length || info.IntegrityMode is not (Pkcs12IntegrityMode.None or Pkcs12IntegrityMode.Password))
            throw new InvalidDataException("保存的接收凭据无效，请手动重新创建连接码。");
        string? password = null;
        if (info.IntegrityMode == Pkcs12IntegrityMode.Password)
        {
            if (info.VerifyMac("")) password = "";
            else if (!info.VerifyMac((string?)null)) throw new InvalidDataException("保存的接收凭据无效，请手动重新创建连接码。");
        }
        foreach (var contents in info.AuthenticatedSafe)
        {
            if (contents.ConfidentialityMode == Pkcs12ConfidentialityMode.Password) contents.Decrypt(password);
            foreach (var bag in contents.GetBags())
            {
                if (bag is not (Pkcs12ShroudedKeyBag or Pkcs12KeyBag)) continue;
                var key = ECDsa.Create();
                try
                {
                    if (bag is Pkcs12ShroudedKeyBag encrypted)
                    {
                        key.ImportEncryptedPkcs8PrivateKey(password.AsSpan(), encrypted.EncryptedPkcs8PrivateKey.Span, out consumed);
                        if (consumed != encrypted.EncryptedPkcs8PrivateKey.Length) throw new CryptographicException();
                    }
                    else
                    {
                        var plain = (Pkcs12KeyBag)bag;
                        key.ImportPkcs8PrivateKey(plain.Pkcs8PrivateKey.Span, out consumed);
                        if (consumed != plain.Pkcs8PrivateKey.Length) throw new CryptographicException();
                    }
                    if (CryptographicOperations.FixedTimeEquals(key.ExportSubjectPublicKeyInfo(), certificate.PublicKey.ExportSubjectPublicKeyInfo()))
                        return key;
                }
                catch { key.Dispose(); throw; }
                key.Dispose();
            }
        }
        throw new InvalidDataException("保存的接收凭据无效，请手动重新创建连接码。");
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

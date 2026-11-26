using FileBridge.Core;
using FileBridge.Desktop;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FileBridge.Tests;

internal static class LifetimeChecks
{
    public static async Task RunAsync(string root)
    {
        var freshIdentity = ReceiverIdentity.Create();
        var freshPfx = freshIdentity.Pfx.ToArray();
        using (var transport = freshIdentity.LoadCertificate())
        {
            Check(transport.Extensions.All(extension => extension.Oid?.Value is { Length: > 0 } oid
                && oid.Split('.').All(arc => uint.TryParse(arc, out _))), "TLS certificate contains Windows-incompatible extension OIDs");
            Check(transport.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages
                .Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"), "TLS server authentication usage was lost");
            Check(freshIdentity.PublicKeyPin && TransferSender.CertificateMatches(freshIdentity.InvitationFor("127.0.0.1", 8787), transport),
                "Windows-compatible TLS certificate changed the persistent public key");
        }
        Check(freshIdentity.Pfx.SequenceEqual(freshPfx), "transport compatibility changed saved credentials");
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            foreach (var decoy in new[] { false, true })
            {
                var request = new CertificateRequest("CN=FileBridge", key, HashAlgorithmName.SHA256);
                if (decoy)
                {
                    var payload = new AsnWriter(AsnEncodingRules.DER);
                    payload.WriteObjectIdentifier("2.25.113014805965853602799128767528523782477");
                    request.CertificateExtensions.Add(new X509Extension(new Oid("1.2.3.4", null), payload.Encode(), false));
                }
                using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
                var unmarked = new ReceiverIdentity(freshIdentity.Token, certificate.Export(X509ContentType.Pfx));
                Check(!unmarked.PublicKeyPin, "absent mode marker or decoy inside another extension enabled public-key pinning");
            }
        }
        var now = DateTimeOffset.UtcNow;
        var legacyMarked = new ReceiverIdentity(new string('a', 64),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-fb3.pfx")));
        var legacyMarkedPfx = legacyMarked.Pfx.ToArray();
        var legacyMarkedCode = legacyMarked.InvitationFor("127.0.0.1", 8787).Encode();
        Check(legacyMarked.PublicKeyPin && legacyMarkedCode.StartsWith("fb3_"), "legacy UUID marker was lost");
        using (var transport = legacyMarked.LoadCertificate())
        {
            Check(TransferSender.CertificateMatches(Invitation.Parse(legacyMarkedCode), transport),
                "legacy UUID-marked identity changed its key during compatibility loading");
            Check(transport.Extensions.All(extension => !string.IsNullOrEmpty(extension.Oid?.Value)), "legacy OID leaked into TLS");
        }
        Check(legacyMarked.Pfx.SequenceEqual(legacyMarkedPfx)
            && legacyMarked.InvitationFor("127.0.0.1", 8787).Encode() == legacyMarkedCode, "legacy credentials or code were modified");
        await using (var legacyMarkedReceiver = await TransferReceiver.StartAsync("127.0.0.1", 0,
            Path.Combine(root, "legacy-marked-received"), identity: legacyMarked))
        {
            var legacySource = Path.Combine(root, "legacy-marked-source.txt");
            await File.WriteAllTextAsync(legacySource, "existing fb3 identity works on Windows");
            await TransferSender.SendAsync([new(legacySource, "verified.txt", new FileInfo(legacySource).Length)],
                legacyMarkedReceiver.Invitation.Encode(), null, default);
        }
        var identity = ReceiverIdentity.Create(now.AddYears(-3), now.AddYears(-2));
        var originalPfx = identity.Pfx.ToArray();
        var invite = identity.InvitationFor("127.0.0.1", 8787);
        var code = invite.Encode();
        Check(code.StartsWith("fb3_", StringComparison.Ordinal) && Invitation.Parse(code).PublicKeyPin, "long-lived code mode lost");
        using var original = identity.LoadStoredCertificate();
        var originalCertificatePin = Wire.Hash(original.RawData);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ReceiverIdentity>(System.Text.Json.JsonSerializer.Serialize(identity))!;
        Check(roundTripped.InvitationFor("127.0.0.1", 8787).Encode() == code, "profile round trip lost the persistent pin mode");
        using (var certificates = new ReceiverCertificateCache(identity))
        {
            foreach (var date in new[] { now, now.AddDays(8), now.AddYears(1), now.AddYears(20) })
            {
                var certificate = certificates.GetCertificate(date);
                Check(TransferSender.CertificateMatches(invite, certificate, date), "same-key renewal invalidated the long-lived code");
                Check(!TransferSender.CertificateMatches(invite with { Pin = new string('0', 64) }, certificate, date), "wrong public key accepted");
                Check(!TransferSender.CertificateMatches(invite with { PublicKeyPin = false }, certificate, date), "pin algorithm silently downgraded");
                Check(!TransferSender.CertificateMatches(invite, certificate, date.AddYears(2)), "expired transport certificate accepted");
                Check(!TransferSender.CertificateMatches(invite, certificate, new DateTimeOffset(certificate.NotBefore.ToUniversalTime()).AddSeconds(-1)),
                    "not-yet-valid transport certificate accepted");
                Check(Wire.Hash(certificate.RawData) != originalCertificatePin, "expired transport certificate was not replaced");
                Check(identity.InvitationFor("127.0.0.1", 8787).Encode() == code, "renewal changed the invitation");
            }
        }
        Check(identity.Pfx.SequenceEqual(originalPfx), "runtime certificate cache changed caller credentials");

        var storePath = Path.Combine(root, "lifetime-settings", "connections.json");
        var store = new ConnectionStore(storePath);
        // A v2.4 certificate has no long-lived marker; do not migrate its code without a manual action.
        var legacy = ReceiverIdentity.Create(now.AddDays(-2), now.AddDays(-1), publicKeyPin: false);
        var legacyCode = legacy.InvitationFor("127.0.0.1", 8787).Encode();
        store.SavePreferences(new());
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(new ConnectionPreferences(Identity: legacy))
            .Replace(",\"PublicKeyPin\":false", "", StringComparison.Ordinal);
        File.WriteAllText(storePath, legacyJson);
        Check(store.GetIdentity().InvitationFor("127.0.0.1", 8787).Encode() == legacyCode, "legacy code auto-migrated");
        try { using var expired = store.GetIdentity().LoadCertificate(); throw new Exception("expired legacy certificate accepted"); }
        catch (InvalidDataException) { }
        Check(store.GetIdentity().Token == legacy.Token, "expired legacy identity was automatically replaced");
        var migrated = store.GetIdentity(recreate: true);
        Check(migrated.PublicKeyPin && migrated.Token != legacy.Token && migrated.InvitationFor("127.0.0.1", 8787).Encode().StartsWith("fb3_"),
            "manual legacy upgrade did not create a long-lived identity");

        var source = Path.Combine(root, "lifetime-source.bin");
        await File.WriteAllTextAsync(source, "same key, renewed certificate, unchanged connection code");
        var destination = Path.Combine(root, "lifetime-received");
        var first = await TransferReceiver.StartAsync("127.0.0.1", 0, destination, identity: identity);
        var savedInvite = first.Invitation;
        var port = new Uri(savedInvite.Url).Port;
        await TransferSender.SendAsync([new(source, "before-restart.txt", new FileInfo(source).Length)], savedInvite.Encode(), null, default);
        await first.DisposeAsync();
        await using (var restarted = await TransferReceiver.StartAsync("127.0.0.1", port, destination, identity: identity))
        {
            Check(restarted.Invitation == savedInvite, "new TLS certificate changed saved code after restart");
            await TransferSender.SendAsync([new(source, "after-restart.txt", new FileInfo(source).Length)], savedInvite.Encode(), null, default);
            Check(await Wire.FileHashAsync(source, default) == await Wire.FileHashAsync(Path.Combine(destination, "after-restart.txt"), default),
                "HTTPS transfer after renewal differs");
        }

        var validLegacy = ReceiverIdentity.Create(now.AddMinutes(-1), now.AddDays(7), publicKeyPin: false);
        await using var oldReceiver = await TransferReceiver.StartAsync("127.0.0.1", 0, Path.Combine(root, "legacy-compat"), identity: validLegacy);
        Check(oldReceiver.Invitation.Encode().StartsWith("fb2_"), "valid legacy code unexpectedly upgraded");
        await TransferSender.SendAsync([new(source, "legacy.txt", new FileInfo(source).Length)], oldReceiver.Invitation.Encode(), null, default);
        Console.WriteLine("PASS long-lived fb3 / same code across 20 years and TLS renewal / certificate dates and pin enforced / manual-only legacy migration / real HTTPS restart / fb2 compatibility");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}

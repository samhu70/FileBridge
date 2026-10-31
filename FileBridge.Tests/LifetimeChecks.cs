using FileBridge.Core;
using FileBridge.Desktop;

namespace FileBridge.Tests;

internal static class LifetimeChecks
{
    public static async Task RunAsync(string root)
    {
        var now = DateTimeOffset.UtcNow;
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

using System.Text.Json;
using FileBridge.Core;
using FileBridge.Desktop;

namespace FileBridge.Tests;

internal static class ConnectionChecks
{
    public static async Task RunAsync(string root)
    {
        var path = Path.Combine(root, "connection-state", "connections.json");
        var store = new ConnectionStore(path);
        var identity = store.GetIdentity();
        store.SavePreferences(new("saved sender input", "127.0.0.1", "8787", "destination", identity));
        var reopenedStore = new ConnectionStore(path);
        var saved = reopenedStore.Load();
        Check(saved is { SendCode: "saved sender input", Address: "127.0.0.1", Port: "8787", Directory: "destination" }, "preferences lost");
        Check(reopenedStore.GetIdentity().InvitationFor("127.0.0.1", 8787) == identity.InvitationFor("127.0.0.1", 8787), "stored identity changed");
        if (!OperatingSystem.IsWindows())
        {
            Check(File.GetUnixFileMode(path) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "credentials are not owner-only");
            Check(File.GetUnixFileMode(Path.GetDirectoryName(path)!) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                "credential directory is not owner-only");
        }

        using (var lease = store.AcquireReceiverLease())
        {
            try { using var conflicting = reopenedStore.AcquireReceiverLease(); throw new Exception("receiver profile can be used twice"); }
            catch (IOException) { }
        }
        using (var reacquired = reopenedStore.AcquireReceiverLease()) { }

        var source = Path.Combine(root, "identity-source.bin");
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Wire.ChunkSize + 17);
        await File.WriteAllBytesAsync(source, bytes);
        var manifest = new FileManifest("saved-code.bin", bytes.Length, Wire.Hash(bytes));
        var destination = Path.Combine(root, "identity-received");
        var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, destination, identity: identity);
        var invitation = receiver.Invitation;
        var port = new Uri(invitation.Url).Port;
        using (var client = TransferSender.CreateClient(invitation))
        {
            await Begin(client, manifest);
            var first = bytes[..Wire.ChunkSize];
            await TransferSender.RequestAsync(client, $"v2/chunk?id={manifest.Id}&offset=0", new ByteArrayContent(first), default, Wire.Hash(first));
        }
        await receiver.DisposeAsync();
        await using (var restarted = await TransferReceiver.StartAsync("127.0.0.1", port, destination, identity: reopenedStore.GetIdentity()))
        {
            Check(restarted.Invitation == invitation, "receiver restart changed a valid code");
            using var client = TransferSender.CreateClient(Invitation.Parse(invitation.Encode()));
            Check((await Begin(client, manifest)).Offset == Wire.ChunkSize, "saved code lost partial data");
            await TransferSender.SendAsync([new(source, manifest.Name, bytes.Length)], invitation.Encode(), null, default);
            Check(await Wire.FileHashAsync(Path.Combine(destination, manifest.Name), default) == manifest.Hash, "restart/resume checksum differs");
        }

        var rotated = store.GetIdentity(recreate: true);
        store.SavePreferences(saved); // A stale window must not restore the revoked credentials.
        Check(store.GetIdentity().Token == rotated.Token && rotated.Token != identity.Token, "stale preference save undid code rotation");
        await using (var replacement = await TransferReceiver.StartAsync("127.0.0.1", port, destination, identity: rotated))
        {
            using var oldClient = TransferSender.CreateClient(invitation);
            try { await Begin(oldClient, manifest); throw new Exception("old certificate pin accepted"); }
            catch (HttpRequestException) { }
            using var oldToken = TransferSender.CreateClient(replacement.Invitation with { Token = invitation.Token });
            try { await Begin(oldToken, manifest); throw new Exception("old token accepted"); }
            catch (TransferException error) when (error.Status == 401) { }
            await TransferSender.SendAsync([new(source, manifest.Name, bytes.Length)], replacement.Invitation.Encode(), null, default);
        }

        var expired = ReceiverIdentity.Create(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
        File.WriteAllText(path, JsonSerializer.Serialize(saved with { Identity = expired }));
        Check(!expired.IsUsable && store.GetIdentity() is { IsUsable: true } renewed && renewed.Token != expired.Token,
            "expired identity was reused");
        File.WriteAllText(path, "invalid JSON");
        try { store.GetIdentity(); throw new Exception("corrupt profile silently accepted"); }
        catch (InvalidDataException) { }
        Check(store.GetIdentity(recreate: true).IsUsable, "manual recreate did not repair corrupt profile");
        var prior = store.GetIdentity().Token;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.SavePreferences(saved))));
        Check(store.GetIdentity().Token == prior, "concurrent preferences changed identity");
        Check(identity.InvitationFor("127.0.0.1", port) != identity.InvitationFor("127.0.0.2", port), "address change did not change code");
        Check(!new ReceiverIdentity("invalid", [1]).IsUsable, "invalid identity accepted");
        Console.WriteLine("PASS saved credentials/preferences / old code survives HTTPS restart and resume / manual rotation rejects old pin+token / expiry+corruption / permissions / stale and concurrent saves");
    }

    private static async Task<ResumeState> Begin(HttpClient client, FileManifest manifest)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(manifest));
        content.Headers.ContentType = new("application/json");
        return await TransferSender.RequestAsync(client, "v2/begin", content, default);
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}

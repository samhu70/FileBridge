using System.Text.Json;
using FileBridge.Core;

namespace FileBridge.Tests;

internal static class ProtocolChecks
{
    public static void Run()
    {
        // Fixed v2 fixtures protect invitation encoding and existing .part file identities.
        var manifest = new FileManifest("folder/sample.bin", 123, new string('a', 64));
        Check(manifest.Id == "395f6e279077b9c26cb5f8d6568bce7aefbb6002e2540c8bc6baae44c287d01d", "v2 partial-file identity changed");
        Check(JsonSerializer.Serialize(manifest) == """
{"Name":"folder/sample.bin","Size":123,"Hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
""", "v2 manifest fields or order changed");
        var invite = new Invitation("https://127.0.0.1:8787", new string('b', 64), new string('c', 64));
        const string code = "fb2_eyJVcmwiOiJodHRwczovLzEyNy4wLjAuMTo4Nzg3IiwiVG9rZW4iOiJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiIiwiUGluIjoiY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjYyJ9";
        Check(invite.Encode() == code && Invitation.Parse(code) == invite, "fb2 invitation compatibility changed");
        var permanent = invite with { PublicKeyPin = true };
        Check(permanent.Encode() == "fb3_" + code[4..] && Invitation.Parse(permanent.Encode()) == permanent,
            "fb3 public-key pin round trip failed");
        var dual = permanent with { PublicUrl = "https://receiver.example.ts.net:8443" };
        Check(Invitation.Parse(dual.Encode()) == dual, "dual invitation round trip failed");
        var legacyView = JsonSerializer.Deserialize<LegacyInvitation>(JsonSerializer.Serialize(dual));
        Check(legacyView is not null && legacyView.Url == invite.Url && legacyView.Token == invite.Token && legacyView.Pin == invite.Pin,
            "dual invitation lost fields understood by old clients");
        foreach (var invalid in new[] { "", "http://example.com", "https://user:secret@example.com", "https://example.com/file",
            "https://example.com/?token=x", "https://example.com/#x", "https://example.com:0" })
        {
            try { Invitation.Parse((dual with { PublicUrl = invalid }).Encode()); throw new Exception("invalid public endpoint accepted"); }
            catch (InvalidDataException) { }
        }
        Check(JsonSerializer.Serialize(new ResumeState(123, "prefix", true, 4)) == """
{"Offset":123,"Prefix":"prefix","Done":true,"ParallelChunks":4}
""", "v2 resume fields changed");
        var legacy = JsonSerializer.Deserialize<ResumeState>("""
{"Offset":123,"Prefix":"prefix","Done":false}
""");
        Check(legacy is { Offset: 123, Prefix: "prefix", Done: false, ParallelChunks: 1 }, "legacy resume defaults changed");
        Check(Wire.ChunkSize == 4 * 1024 * 1024 && Wire.DefaultParallelChunks == 4 && Wire.MaxParallelChunks == 10
            && Wire.PartsDirectory == ".filebridge-parts", "v2 transfer defaults changed");
        var oldOffer = JsonSerializer.Deserialize<ReverseOffer>(JsonSerializer.Serialize(new
            { Id = new string('a', 64), Files = new[] { manifest }, ParallelChunks = 4 }));
        Check(oldOffer?.ChunkSize == Wire.ChunkSize, "legacy reverse offer lost its four-MiB block size");
        Console.WriteLine("PASS fixed v2 protocol fixtures / fb2 invitation / partial-file identity / legacy resume defaults");
    }

    private sealed record LegacyInvitation(string Url, string Token, string Pin);

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}

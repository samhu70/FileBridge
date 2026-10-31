using FileBridge.Core;

namespace FileBridge.Tests;

internal static class AdaptiveStreamChecks
{
    public static async Task RunAsync(string root)
    {
        var cases = new (long Size, int Limit, int Expected)[]
        {
            (0, 10, 1), (1, 10, 1), (Wire.ChunkSize - 1, 10, 1), (Wire.ChunkSize, 10, 1),
            (Wire.ChunkSize + 1, 10, 2), (Wire.ChunkSize * 2L, 10, 2), (Wire.ChunkSize * 2L + 1, 10, 3),
            (Wire.ChunkSize * 10L + 1, 4, 4), (Wire.ChunkSize * 10L + 1, 1, 1)
        };
        Check(TransferSender.StreamsForSize(long.MaxValue, 10) == 10, "large size overflowed stream calculation");
        var received = new List<TransferProgress>();
        var gate = new object();
        await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, Path.Combine(root, "adaptive-received"),
            new InlineProgress(p => { lock (gate) received.Add(p); }));
        for (var i = 0; i < cases.Length; i++)
        {
            var (size, limit, expected) = cases[i];
            Check(TransferSender.StreamsForSize(size, limit) == expected, "wrong stream count at a size boundary");
            var source = Path.Combine(root, "adaptive-source.bin");
            await using (var file = File.Create(source)) file.SetLength(size);
            var sent = new List<TransferProgress>();
            lock (gate) received.Clear();
            await TransferSender.SendAsync([new(source, $"case-{i}.bin", size)], receiver.Invitation.Encode(),
                new InlineProgress(p => sent.Add(p)), default, limit);
            Check(sent.Where(p => p.ParallelChunks > 0).All(p => p.ParallelChunks == expected), "sender reported wrong effective streams");
            lock (gate) Check(received.Count > 0 && received.All(p => p.ParallelChunks == expected), "receiver negotiated wrong effective streams");
        }
        Console.WriteLine("PASS actual adaptive transfers / empty and <=4MiB single stream / chunk boundaries / user cap / serial override / overflow");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}

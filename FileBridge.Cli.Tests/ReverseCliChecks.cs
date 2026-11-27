using System.Security.Cryptography;
using FileBridge.Core;

internal static class ReverseCliChecks
{
    public static async Task RunAsync(string root, CancellationToken ct)
    {
        var source = Path.Combine(root, "reverse-source");
        Directory.CreateDirectory(Path.Combine(source, "子目录"));
        var bytes = RandomNumberGenerator.GetBytes(2 * Wire.ChunkSize + 19);
        await File.WriteAllBytesAsync(Path.Combine(source, "子目录", "续传.bin"), bytes, ct);
        await File.WriteAllTextAsync(Path.Combine(source, "空文件.txt"), "", ct);
        var files = TransferSender.Collect([source]);
        await using var peer = await TransferReceiver.StartAsync("127.0.0.1", 0, Path.Combine(root, "reverse-peer"), ct: ct);
        var code = peer.Invitation.Encode();
        var destination = Path.Combine(root, "reverse-cwd");
        Directory.CreateDirectory(destination);

        // Receive starts before Desktop's reverse-send offer and uses outgoing connections only.
        await using (var receiver = new CliProcess(["receive", "--code", "-", "--parallel", "2"], destination))
        {
            await receiver.InputAsync(code);
            await receiver.WaitForErrorAsync("WaitingForSender:", ct);
            var offering = peer.SendReverseAsync(files, null, ct, 4);
            Check(await receiver.ExitAsync(ct) == 0, "CLI reverse receive failed: " + receiver.Errors);
            await offering.WaitAsync(ct);
            Check(await receiver.ReadOutputAsync() == "", "reverse receive emitted a local invitation");
            foreach (var file in files)
                Check(await Wire.FileHashAsync(Path.Combine(destination, file.RelativeName), ct) == await Wire.FileHashAsync(file.LocalPath, ct),
                    "reverse receive file checksum differs");
        }
        Console.WriteLine("PASS CLI reverse receive / wait before peer offer / stdin code / current directory / Unicode and empty files / SHA-256 / no local invitation");

        var resumeDirectory = Path.Combine(root, "reverse-resume");
        var large = files.Single(f => f.Size > 0);
        var manifest = new FileManifest(large.RelativeName, bytes.Length, Wire.Hash(bytes));
        var prefix = bytes[..Wire.ReverseChunkSize];
        await using (var local = TransferReceiver.CreateLocal(resumeDirectory, null))
        {
            await local.BeginLocalAsync(manifest, 1, false, ct);
            await local.WriteLocalAsync(manifest.Id, 0, prefix, Wire.Hash(prefix), ct);
        }
        var partial = Path.Combine(resumeDirectory, Wire.PartsDirectory, manifest.Id + ".part");
        if (!OperatingSystem.IsWindows())
        {
            await using var waiting = new CliProcess("receive", "--code", code, "--directory", resumeDirectory);
            await waiting.WaitForErrorAsync("WaitingForSender:", ct);
            await waiting.StopAsync(ct);
            Check(new FileInfo(partial).Length == prefix.Length, "stopping reverse wait lost partial data");

            await using var input = new CliProcess("receive", "--code", "-");
            await input.WaitForErrorAsync("Reading connection code", ct);
            await input.StopAsync(ct);
        }
        using (var offerStop = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var offering = peer.SendReverseAsync([large], null, offerStop.Token, 2);
            try
            {
                await using var resumed = new CliProcess("receive", "--code", code, "--directory", resumeDirectory, "--parallel", "1");
                Check(await resumed.ExitAsync(ct) == 0, "reverse resume failed: " + resumed.Errors);
                await offering.WaitAsync(ct);
                Check(resumed.Errors.Contains("VerifyingResume:") && !resumed.Errors.Contains("ResendingCorrupt:"), "reverse prefix was not verified");
                Check(await Wire.FileHashAsync(Path.Combine(resumeDirectory, manifest.Name), ct) == manifest.Hash, "reverse resume checksum");
            }
            finally
            {
                offerStop.Cancel();
                try { await offering; } catch (OperationCanceledException) when (offerStop.IsCancellationRequested) { }
            }
        }
        Console.WriteLine("PASS CLI reverse waiting cancellation / stdin cancellation / persisted prefix / explicit directory / verified resume");

        foreach (var rejected in new[] { peer.Invitation with { Token = new string('0', 64) }, peer.Invitation with { Pin = new string('0', 64) } })
        {
            await using var denied = new CliProcess("receive", "--code", rejected.Encode(), "--directory", resumeDirectory);
            Check(await denied.ExitAsync(ct) == 1, "reverse receive accepted invalid credentials or retried forever");
        }
        Console.WriteLine("PASS CLI reverse invalid token / TLS pin rejected");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

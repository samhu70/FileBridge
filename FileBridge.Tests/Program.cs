using System.Net;
using System.Net.NetworkInformation;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileBridge.Core;
using FileBridge.Desktop;
using FileBridge.Tests;

var parent = Environment.GetEnvironmentVariable("FILEBRIDGE_TEST_ROOT") ?? Path.GetTempPath();
var root = Path.Combine(parent, "filebridge-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args.Contains("--ui")) { UiChecks.Run(root); return; }
    if (args.Contains("--reverse")) { await ReverseChecks.RunAsync(root); return; }
    ProtocolChecks.Run();
    await ConnectionChecks.RunAsync(root);
    await LifetimeChecks.RunAsync(root);
    await AdaptiveStreamChecks.RunAsync(root);
    await ReverseChecks.RunAsync(root);
    LocalizationChecks();
    ReceiveDisplayChecks();
    AddressDiscovery();
    await LanBinding();
    await Boundaries();
    await RestartResume();
    await PauseResume();
    await RetryResume();
    await ParallelChunks();
    await StreamSettings();
    await PartialChunkTelemetry();
    if (args.Contains("--large")) await LargeTransfer();
    Console.WriteLine("ALL PASS");
}
finally
{
    // Finder can recreate .DS_Store while a folder opened by the native UI check is being deleted.
    for (var attempt = 0; ; attempt++)
    {
        try { Directory.Delete(root, recursive: true); break; }
        catch (IOException) when (attempt < 2 && Directory.Exists(root)
            && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).All(f => Path.GetFileName(f) == ".DS_Store"))
        { await Task.Delay(100); }
    }
}

void LocalizationChecks()
{
    var settings = Path.Combine(root, "language", "language.txt");
    var chinese = new Localization(settings, CultureInfo.GetCultureInfo("zh-CN"));
    var english = new Localization(settings, CultureInfo.GetCultureInfo("en-AU"));
    Check(chinese.Language == "zh" && english.Language == "en", "system language defaults incorrect");
    Check(english.ResourceKeys("en").SequenceEqual(english.ResourceKeys("zh")), "language catalogs differ");
    foreach (var key in english.Keys)
    {
        var en = english.Text(key);
        var zh = chinese.Text(key);
        Check(key == "LanguageAction" || !Regex.IsMatch(en, @"\p{IsCJKUnifiedIdeographs}"), "untranslated English resource: " + key);
        Check(Regex.Matches(en, @"\{\d+\}").Select(m => m.Value).Order().SequenceEqual(
            Regex.Matches(zh, @"\{\d+\}").Select(m => m.Value).Order()), "format placeholders differ: " + key);
    }
    Check(english.CoreText("发送中") == "Sending", "progress not translated");
    Check(english.CoreText("连接中断，自动续传 3/10") == "Connection interrupted; resuming 3/10", "retry count not translated");
    Check(english.CoreText("接收出错：数据块校验失败，未写入。") == "Receive error: Chunk verification failed. No data was written.", "nested receive error not translated");
    const string fileName = "发送中/中文文件.txt";
    Check(english.CoreText("文件名重复或无效：" + fileName).EndsWith(fileName, StringComparison.Ordinal), "translation altered filename suffix");
    Check(chinese.CoreText("校验续传进度") == "校验续传进度", "canonical Chinese progress changed");
    Check(english.CoreText("Operating system error 42") == "Operating system error 42", "unknown OS detail altered");
    Check(english.SetLanguage("en"), "language preference not saved");
    Check(new Localization(settings, CultureInfo.GetCultureInfo("zh-CN")).Language == "en", "language preference did not override system language");
    Check(english.SetLanguage("zh") && english.Text("SendTab") == "发送文件", "live language switch failed");
    Check(new Localization(settings, CultureInfo.GetCultureInfo("en-US")).Language == "zh", "Chinese preference did not reload");
    File.WriteAllText(settings, "invalid");
    Check(new Localization(settings, CultureInfo.GetCultureInfo("en-US")).Language == "en", "invalid preference did not fall back");
    var unwritable = new Localization(root, CultureInfo.GetCultureInfo("en-US"));
    Check(!unwritable.SetLanguage("zh") && unwritable.Language == "zh", "preference write failure prevented language switch");
    Console.WriteLine("PASS localization / catalog parity / progress and errors / filenames preserved / preference round trip and failure");
}

void ReceiveDisplayChecks()
{
    var en = new Localization(Path.Combine(root, "display-en.txt"), CultureInfo.GetCultureInfo("en-US"));
    var zh = new Localization(Path.Combine(root, "display-zh.txt"), CultureInfo.GetCultureInfo("zh-CN"));
    var progress = new TransferProgress("中文.bin", TransferStage.Receiving, 1024, 11 * 1024, 1024, 3);
    Check(TransferDisplay.ReceiveSpeed(progress, true, 1000) == 1024, "active receive speed missing");
    Check(TransferDisplay.ReceiveSpeed(progress, true, 5000) == 0, "stale receive speed retained");
    Check(TransferDisplay.ReceiveSpeed(progress, false, 0) == 0, "stopped receiver retained speed");
    Check(TransferDisplay.ReceiveSpeed(progress with { BytesPerSecond = double.NaN }, true, 0) == 0, "invalid speed accepted");
    var sendingSample = progress with { Stage = TransferStage.Sending };
    Check(TransferDisplay.SendSpeed(sendingSample, true, 6000) == 1024,
        "sparse sender progress lost speed and ETA after five seconds despite an active send");
    Check(TransferDisplay.SendSpeed(sendingSample, false, 0) == 0, "paused sender retained speed");
    Check(TransferDisplay.SendSpeed(sendingSample, true, 300000) == 0, "sender retained an expired sample beyond the protocol idle deadline");
    Check(TransferDisplay.SendSpeed(sendingSample with { BytesPerSecond = double.NaN }, true, 0) == 0,
        "sender accepted invalid speed");
    Check(TransferDisplay.ReceiveEstimate(progress, 1024, en) == "Time left (this file): about 10 s", "receive seconds incorrect");
    Check(TransferDisplay.ReceiveEstimate(progress with { Total = 66 * 1024 }, 1024, en).EndsWith("1 min 5 s"), "receive minutes incorrect");
    Check(TransferDisplay.ReceiveEstimate(progress with { Total = 3666 * 1024 }, 1024, en).EndsWith("1 h 1 min"), "receive hours incorrect");
    Check(TransferDisplay.ReceiveEstimate(progress, 0, zh) == zh.Text("TimeWaiting"), "zero-speed ETA did not wait");
    Check(TransferDisplay.ReceiveEstimate(progress with { Completed = progress.Total }, 1024, en) == en.Text("TimeVerifying"), "received data reported complete before verification");
    Check(TransferDisplay.ReceiveEstimate(progress with { Stage = TransferStage.VerifyingFile }, 0, en) == en.Text("TimeVerifying"), "verification ETA incorrect");
    Check(TransferDisplay.ReceiveEstimate(progress with { Stage = TransferStage.ReceiveComplete }, 0, zh) == zh.Text("TimeComplete"), "completion ETA incorrect");
    Check(TransferDisplay.SendEstimate(progress with { Stage = TransferStage.Sending }, 1024, en)
        == "Time left (this file): about 10 s", "sender ETA missing");
    Check(TransferDisplay.SendEstimate(sendingSample, 1024, en, recent: true)
        == "Time left (this file, based on recent speed): about 10 s", "recent sender estimate was not labelled honestly");
    Check(TransferDisplay.SendEstimate(progress with { Stage = TransferStage.Sending, Completed = progress.Total }, 0, zh)
        == zh.Text("SendTimeVerifying"), "sender claimed verification before peer receipt");
    Check(TransferDisplay.SendEstimate(progress with { Stage = TransferStage.SendComplete }, 0, en)
        == en.Text("SendTimeComplete"), "sender verified ETA completion missing");
    Check(TransferDisplay.SendEstimate(progress with { Stage = TransferStage.Sending }, 0, zh)
        == zh.Text("TimeWaiting"), "stale sender ETA retained");
    Check(TransferDisplay.ParallelMode(3, en) == "Parallel: on (3 streams)", "parallel mode not rendered");
    Check(TransferDisplay.ParallelMode(1, zh) == "并行：已关闭（1 路）", "single-stream mode not rendered");
    Check(TransferDisplay.ParallelMode(0, en) == en.Text("ParallelNegotiating"), "unnegotiated mode not rendered");
    Check(TransferDisplay.Bytes(1L << 20, en.Culture) == "1 MiB", "byte formatting changed");
    foreach (var stage in Enum.GetValues<TransferStage>())
    {
        var sample = progress with { Stage = stage, RetryAttempt = 3, RetryLimit = 10, Error = "数据块校验失败，未写入。" };
        Check(!string.IsNullOrWhiteSpace(TransferDisplay.StageText(sample, en)), "missing English stage: " + stage);
        Check(!string.IsNullOrWhiteSpace(TransferDisplay.StageText(sample, zh)), "missing Chinese stage: " + stage);
    }
    Check(TransferDisplay.StageText(progress, en) == "Receiving", "typed receiving stage not translated");
    Check(TransferDisplay.StageText(progress with { Stage = TransferStage.Retrying, RetryAttempt = 3, RetryLimit = 10 }, en)
        == "Connection interrupted; resuming 3/10", "typed retry fields not rendered");
    Check(TransferDisplay.StageText(progress with { Stage = TransferStage.ReceiveError, Error = "数据块校验失败，未写入。" }, en)
        == "Receive error: Chunk verification failed. No data was written.", "typed receive error not translated");
    Check(TransferDisplay.StageText(progress with { Stage = TransferStage.ReceiveError, Error = "未知系统错误" }, zh)
        == "接收出错：未知系统错误", "Chinese receive error changed");
    Console.WriteLine("PASS receive display / typed stages / structured retry and error / speed expiration / per-file ETA / both languages");
}

void AddressDiscovery()
{
    static (IPAddress Address, bool HasGateway) Candidate(string address, bool gateway = false) => (IPAddress.Parse(address), gateway);
    Check(NetworkAddressDiscovery.SelectAddress([Candidate("192.168.1.5", true), Candidate("100.65.24.64")]).ToString() == "100.65.24.64", "Tailscale did not take priority");
    Check(NetworkAddressDiscovery.SelectAddress([Candidate("172.17.0.1"), Candidate("192.168.1.5", true)]).ToString() == "192.168.1.5", "LAN gateway not preferred");
    foreach (var address in new[] { "10.0.0.5", "172.16.0.5", "172.31.255.5", "192.168.1.5" })
        Check(NetworkAddressDiscovery.SelectAddress([Candidate("127.0.0.1"), Candidate(address)]).ToString() == address, "gatewayless private LAN not selected: " + address);
    foreach (var address in new[] { "100.64.0.1", "100.127.255.254" })
        Check(NetworkAddressDiscovery.SelectAddress([Candidate(address)]).ToString() == address, "Tailscale range boundary rejected: " + address);
    var unusable = new[] { "::1", "fd00::1", "127.0.0.2", "169.254.1.2", "0.0.0.0", "8.8.8.8", "172.15.0.1", "172.32.0.1", "100.63.255.255", "100.128.0.1" };
    Check(NetworkAddressDiscovery.SelectAddress(unusable.Select(a => Candidate(a, true))).Equals(IPAddress.Loopback), "unsuitable automatic LAN address selected");
    Check(NetworkAddressDiscovery.SelectAddress([]).Equals(IPAddress.Loopback), "empty network list did not use loopback");
    Check(IPAddress.TryParse(Wire.DiscoverAddress(), out _), "live network discovery failed");
    Console.WriteLine("PASS address discovery / Tailscale priority / private LAN fallback / gateway preference / loopback");
}

async Task LanBinding()
{
    var lan = NetworkAddressDiscovery.SelectAddress(NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.GetAddressBytes() is [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _])
        .Select(a => (a.Address, false)));
    if (IPAddress.IsLoopback(lan)) { Console.WriteLine("SKIP LAN binding: no active private IPv4"); return; }
    var source = Path.Combine(root, "lan-source.txt");
    await File.WriteAllTextAsync(source, "LAN address fallback");
    var destination = Path.Combine(root, "lan-received");
    await using var receiver = await TransferReceiver.StartAsync(lan.ToString(), 0, destination);
    await TransferSender.SendAsync([new(source, "lan.txt", new FileInfo(source).Length)], receiver.Invitation.Encode(), null, default);
    Check(await File.ReadAllTextAsync(Path.Combine(destination, "lan.txt")) == "LAN address fallback", "LAN-bound transfer failed");
    Console.WriteLine("PASS actual private IPv4 listener / HTTPS transfer");
}

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static ByteArrayContent Metadata(FileManifest m)
{
    var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(m));
    content.Headers.ContentType = new("application/json");
    return content;
}
static async Task<ResumeState> Begin(HttpClient c, FileManifest m)
    => await TransferSender.RequestAsync(c, "v2/begin", Metadata(m), default);
static async Task Chunk(HttpClient c, FileManifest m, long offset, byte[] bytes, bool corrupt = false)
    => await TransferSender.RequestAsync(c, $"v2/chunk?id={m.Id}&offset={offset}", new ByteArrayContent(bytes), default,
        corrupt ? new string('0', 64) : Wire.Hash(bytes));
static async Task ExpectFailure(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception error) when (error is TransferException or InvalidDataException or HttpRequestException) { return; }
    throw new Exception(message);
}
async Task Boundaries()
{
    var dir = Path.Combine(root, "security");
    await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, dir);
    var invite = receiver.Invitation;
    using var client = TransferSender.CreateClient(invite);
    var m = new FileManifest("正常.txt", 3, Wire.Hash("abc"u8));
    using var badToken = TransferSender.CreateClient(invite with { Token = new string('0', 64) });
    await ExpectFailure(() => Begin(badToken, m), "bad token accepted");
    using var badPin = TransferSender.CreateClient(invite with { Pin = new string('0', 64) });
    await ExpectFailure(() => Begin(badPin, m), "bad certificate accepted");
    foreach (var name in new[] { "../escape", "/absolute", "a/../../b", "a\\b", "C:bad", ".filebridge-parts/no", "a/../b", "a//b", "NUL", "folder/CON.txt", "LPT1", "COM¹", "file.", "name ", "bad?name" })
        await ExpectFailure(() => Begin(client, m with { Name = name }), "unsafe name accepted: " + name);
    await Begin(client, m);
    await ExpectFailure(() => Chunk(client, m, 0, "abc"u8.ToArray(), true), "corrupt chunk accepted");
    Check((await Begin(client, m)).Offset == 0, "corrupt chunk advanced offset");
    await Chunk(client, m, 0, "abc"u8.ToArray());
    await File.WriteAllTextAsync(Path.Combine(dir, m.Name), "preserve");
    await ExpectFailure(() => TransferSender.RequestAsync(client, "v2/finish?id=" + m.Id, null, default), "existing file overwritten");
    Check(await File.ReadAllTextAsync(Path.Combine(dir, m.Name)) == "preserve", "existing contents changed");
    var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
    Directory.CreateSymbolicLink(Path.Combine(dir, "escape"), outside);
    await ExpectFailure(() => Begin(client, m with { Name = "escape/file" }), "symbolic link escaped root");
    var emptySource = Path.Combine(root, "empty"); await File.WriteAllBytesAsync(emptySource, []);
    await TransferSender.SendAsync([new(emptySource, "空文件", 0)], invite.Encode(), null, default);
    Check(new FileInfo(Path.Combine(dir, "空文件")).Length == 0, "empty file failed");
    Check(Invitation.Parse(invite.Encode()) == invite, "invitation round trip failed");
    await ExpectFailure(() => Task.Run(() => Invitation.Parse("fb1_old")), "old client code accepted");
    Console.WriteLine("PASS authentication / certificate / corruption / paths / no-overwrite / empty file");
}
async Task RestartResume()
{
    var dir = Path.Combine(root, "restart");
    var source = Path.Combine(root, "source.bin");
    var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize + 12345);
    await File.WriteAllBytesAsync(source, bytes);
    var m = new FileManifest("素材/片段.bin", bytes.Length, Wire.Hash(bytes));
    var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, dir);
    using (var client = TransferSender.CreateClient(receiver.Invitation))
    {
        await Begin(client, m);
        await Chunk(client, m, 0, bytes[..Wire.ChunkSize]);
    }
    await receiver.DisposeAsync();
    await using var restarted = await TransferReceiver.StartAsync("127.0.0.1", 0, dir);
    using var c = TransferSender.CreateClient(restarted.Invitation);
    var state = await Begin(c, m);
    Check(state.Offset == Wire.ChunkSize && state.Prefix == Wire.Hash(bytes.AsSpan(0, Wire.ChunkSize)), "restart lost persisted bytes");
    var events = new List<TransferProgress>();
    await TransferSender.SendAsync([new(source, m.Name, m.Size)], restarted.Invitation.Encode(), new InlineProgress(p => events.Add(p)), default);
    Check((await File.ReadAllBytesAsync(Path.Combine(dir, "素材/片段.bin"))).SequenceEqual(bytes), "restart content differs");
    Check(events.Any(p => p.Completed == Wire.ChunkSize && p.Stage == TransferStage.VerifyingResume), "sender did not resume offset");
    Check(!Directory.EnumerateFiles(Path.Combine(dir, Wire.PartsDirectory)).Any(), "completed partial not cleaned");
    Console.WriteLine("PASS receiver restart / persisted prefix / Unicode folder / final checksum");
}
async Task PauseResume()
{
    var dir = Path.Combine(root, "pause");
    var source = Path.Combine(root, "pause.bin");
    var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize * 2 + 40);
    await File.WriteAllBytesAsync(source, bytes);
    await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, dir);
    using var pause = new CancellationTokenSource();
    try
    {
        await TransferSender.SendAsync([new(source, "pause.bin", bytes.Length)], receiver.Invitation.Encode(),
            new InlineProgress(p => { if (p.Stage == TransferStage.Sending && p.Completed >= Wire.ChunkSize) pause.Cancel(); }), pause.Token);
        throw new Exception("pause did not cancel");
    }
    catch (OperationCanceledException) when (pause.IsCancellationRequested) { }
    Check(!File.Exists(Path.Combine(dir, "pause.bin")), "incomplete file published");
    var m = new FileManifest("pause.bin", bytes.Length, Wire.Hash(bytes));
    // A corrupted persisted prefix must never be silently trusted.
    var partial = Path.Combine(dir, Wire.PartsDirectory, m.Id + ".part");
    await using (var stream = File.Open(partial, FileMode.Open, FileAccess.Write, FileShare.None)) { stream.WriteByte(0x7F); }
    await TransferSender.SendAsync([new(source, m.Name, m.Size)], receiver.Invitation.Encode(), null, default, parallelChunks: 2);
    Check((await File.ReadAllBytesAsync(Path.Combine(dir, m.Name))).SequenceEqual(bytes), "corrupt partial not recovered");
    Console.WriteLine("PASS pause / partial retained / corrupt-prefix recovery");
}
async Task RetryResume()
{
    var bytes = RandomNumberGenerator.GetBytes(4096);
    var source = Path.Combine(root, "retry-source.bin");
    await File.WriteAllBytesAsync(source, bytes);
    var destination = Path.Combine(root, "retry-received");
    var begins = 0;
    var errorReported = false;
    await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, destination,
        new InlineProgress(p =>
        {
            // Return one transient HTTP 503 without a test endpoint or production hook.
            if (p.Stage == TransferStage.WaitingForData && Interlocked.Increment(ref begins) == 1)
                throw new TransferException(503, "temporary receiver failure");
            if (p.Stage == TransferStage.ReceiveError) errorReported = p.Error == "temporary receiver failure";
        }));
    var events = new List<TransferProgress>();
    await TransferSender.SendAsync([new(source, "retry.bin", bytes.Length)], receiver.Invitation.Encode(),
        new InlineProgress(p => events.Add(p)), default);
    var retries = events.Where(p => p.Stage == TransferStage.Retrying).ToArray();
    Check(begins == 2 && retries is [{ RetryAttempt: 1, RetryLimit: 10 }] && errorReported,
        "transient error did not produce structured retry/error progress");
    Check((await File.ReadAllBytesAsync(Path.Combine(destination, "retry.bin"))).SequenceEqual(bytes), "retry contents differ");
    Console.WriteLine("PASS transient HTTP 503 / automatic retry / structured retry and error / checksum");
}

async Task LargeTransfer()
{
    var source = Path.Combine(root, "2GiB.bin");
    await using (var file = File.Create(source)) { file.SetLength(2L << 30); }
    var dir = Path.Combine(root, "large");
    await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, dir);
    using var pause = new CancellationTokenSource();
    var selection = new[] { new TransferFile(source, "2GiB.bin", 2L << 30) };
    try
    {
        await TransferSender.SendAsync(selection, receiver.Invitation.Encode(),
            new InlineProgress(p => { if (p.Stage == TransferStage.Sending && p.Completed >= 128L << 20) pause.Cancel(); }), pause.Token);
        throw new Exception("large pause did not cancel");
    }
    catch (OperationCanceledException) when (pause.IsCancellationRequested) { }
    var resumed = false;
    await TransferSender.SendAsync(selection, receiver.Invitation.Encode(),
        new InlineProgress(p => { if (p.Stage == TransferStage.VerifyingResume && p.Completed >= 128L << 20) resumed = true; }), default);
    Check(resumed, "2GiB transfer restarted instead of resuming");
    Check(await Wire.FileHashAsync(source, default) == await Wire.FileHashAsync(Path.Combine(dir, "2GiB.bin"), default), "2GiB hash differs");
    Console.WriteLine("PASS actual 2GiB HTTPS transfer / pause at 128MiB / resume / SHA-256");
}

async Task ParallelChunks()
{
    var dir = Path.Combine(root, "parallel");
    var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize * 2 + 123);
    var source = Path.Combine(root, "parallel-source.bin");
    await File.WriteAllBytesAsync(source, bytes);
    var m = new FileManifest("并行/乱序.bin", bytes.Length, Wire.Hash(bytes));
    await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, dir);
    using var client = TransferSender.CreateClient(receiver.Invitation);
    Check((await Begin(client, m)).ParallelChunks == 4, "legacy sender default changed");
    var later = Chunk(client, m, Wire.ChunkSize, bytes[Wire.ChunkSize..(Wire.ChunkSize * 2)]);
    await Task.Delay(100);
    Check(!later.IsCompleted, "later chunk acknowledged without a committed prefix");
    Check(new FileInfo(Path.Combine(dir, Wire.PartsDirectory, m.Id + ".part")).Length == 0, "out-of-order chunk changed prefix");
    var tail = Chunk(client, m, Wire.ChunkSize * 2, bytes[(Wire.ChunkSize * 2)..]);
    await Task.WhenAll(later, tail, Chunk(client, m, 0, bytes[..Wire.ChunkSize])).WaitAsync(TimeSpan.FromSeconds(10));
    await TransferSender.RequestAsync(client, "v2/finish?id=" + m.Id, null, default);
    Check((await File.ReadAllBytesAsync(Path.Combine(dir, "并行/乱序.bin"))).SequenceEqual(bytes), "out-of-order file differs");

    var reset = m with { Name = "reset.bin" };
    await Begin(client, reset);
    var obsolete = Chunk(client, reset, Wire.ChunkSize, bytes[Wire.ChunkSize..(Wire.ChunkSize * 2)]);
    await Task.Delay(100);
    Check(!obsolete.IsCompleted, "pending gap request did not wait");
    await Begin(client, reset);
    await ExpectFailure(() => obsolete.WaitAsync(TimeSpan.FromSeconds(10)), "reset accepted an obsolete parallel request");
    var events = new List<TransferProgress>();
    await TransferSender.SendAsync([new(source, reset.Name, bytes.Length)], receiver.Invitation.Encode(),
        new InlineProgress(p => events.Add(p)), default);
    Check(events.Any(p => p.Stage == TransferStage.Sending && p.ParallelChunks == 3), "three-chunk file did not adapt below the default four-stream limit");
    Check(events.Where(p => p.Stage == TransferStage.Sending).Select(p => p.Completed).SequenceEqual(
        events.Where(p => p.Stage == TransferStage.Sending).Select(p => p.Completed).Order()), "parallel progress regressed");
    Check((await File.ReadAllBytesAsync(Path.Combine(dir, reset.Name))).SequenceEqual(bytes), "parallel sender contents differ");

    var legacy = JsonSerializer.Deserialize<ResumeState>("{\"Offset\":0,\"Prefix\":\"\",\"Done\":false}");
    Check(legacy?.ParallelChunks == 1, "legacy receiver did not default to a single stream");
    var outsideWindow = m with { Name = "window.bin", Size = Wire.ChunkSize * 6L };
    await Begin(client, outsideWindow);
    await ExpectFailure(() => Chunk(client, outsideWindow, Wire.ChunkSize * 4L, [1]), "out-of-window chunk accepted");
    Console.WriteLine("PASS parallel chunks / ordered durable prefix / reset cancels pending gap / negotiated streams / legacy fallback");
}
async Task StreamSettings()
{
    var bytes = RandomNumberGenerator.GetBytes(Wire.ChunkSize * 10 + 17);
    var source = Path.Combine(root, "streams-source.bin");
    await File.WriteAllBytesAsync(source, bytes);
    var receiveEvents = new List<TransferProgress>();
    var eventsGate = new object();
    var dir = Path.Combine(root, "stream-settings");
    await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, dir,
        new InlineProgress(p => { lock (eventsGate) receiveEvents.Add(p); }));
    using var client = TransferSender.CreateClient(receiver.Invitation);
    var m = new FileManifest("negotiate.bin", bytes.Length, Wire.Hash(bytes));
    var bounded = await TransferSender.RequestAsync(client, "v2/begin?parallelChunks=99", Metadata(m), default);
    Check(bounded.ParallelChunks == 10, "receiver did not bound requested streams");
    var outside = m with { Name = "ten-stream-window.bin", Size = Wire.ChunkSize * 12L };
    await TransferSender.RequestAsync(client, "v2/begin?parallelChunks=10", Metadata(outside), default);
    await ExpectFailure(() => Chunk(client, outside, Wire.ChunkSize * 10L, [1]), "chunk beyond ten-stream window accepted");
    foreach (var invalid in new[] { "0", "-1", "bad", "1&parallelChunks=2" })
        await ExpectFailure(() => TransferSender.RequestAsync(client, "v2/begin?parallelChunks=" + invalid, Metadata(m), default), "invalid stream count accepted");
    Check((await Begin(client, m)).ParallelChunks == 4, "legacy sender default changed");
    var single = await TransferSender.RequestAsync(client, "v2/begin?parallelChunks=1", Metadata(m), default);
    Check(single.ParallelChunks == 1, "single stream negotiation failed");
    await ExpectFailure(() => Chunk(client, m, Wire.ChunkSize, [1]), "single-stream receiver accepted a parallel gap");
    foreach (var count in Enumerable.Range(1, 10))
    {
        lock (eventsGate) receiveEvents.Clear();
        var events = new List<TransferProgress>();
        var name = $"streams-{count}.bin";
        // Resume some runs, and change the selected count from the previous begin request.
        if (count is 2 or 3 or 10)
        {
            var partial = m with { Name = name };
            await Begin(client, partial);
            await Chunk(client, partial, 0, bytes[..Wire.ChunkSize]);
            lock (eventsGate) receiveEvents.Clear();
        }
        await TransferSender.SendAsync([new(source, name, bytes.Length)], receiver.Invitation.Encode(),
            new InlineProgress(p => events.Add(p)), default, count);
        Check(events.Any(p => p.Stage == TransferStage.Sending) && events.Where(p => p.Stage == TransferStage.Sending).All(p => p.ParallelChunks == count), "sender limit ignored");
        TransferProgress[] received;
        lock (eventsGate) received = receiveEvents.ToArray();
        Check(received.Any(p => p.Stage == TransferStage.Receiving && p.BytesPerSecond > 0 && double.IsFinite(p.BytesPerSecond)), "receive throughput missing");
        Check(received.All(p => p.ParallelChunks == count), "receiver did not match sender count across stages");
        Check(received[^1].Stage == TransferStage.ReceiveComplete, "receive completion missing");
        Check(received.Where(p => p.Stage == TransferStage.Receiving).Select(p => p.Completed).SequenceEqual(
            received.Where(p => p.Stage == TransferStage.Receiving).Select(p => p.Completed).Order()), "receive progress regressed");
        Check((await File.ReadAllBytesAsync(Path.Combine(dir, name))).SequenceEqual(bytes), "configured stream transfer differs");
    }
    foreach (var count in new[] { 0, 11 })
    {
        try
        {
            await TransferSender.SendAsync([], receiver.Invitation.Encode(), null, default, count);
            throw new Exception("invalid sender stream limit accepted");
        }
        catch (ArgumentOutOfRangeException) { }
    }
    Check(TransferSender.NegotiatedStreams(4, 1) == 1 && TransferSender.NegotiatedStreams(4, 2) == 2
        && TransferSender.NegotiatedStreams(2, 4) == 2 && TransferSender.NegotiatedStreams(10, 100) == 10
        && TransferSender.NegotiatedStreams(10, 4) == 4 && TransferSender.NegotiatedStreams(10, 1) == 1
        && TransferSender.NegotiatedStreams(10, 0) == 1, "peer stream limits not respected");
    Console.WriteLine("PASS sender stream selection 1–10 / default 4 / receiver matching / limits / speed / changed-count resume / legacy negotiation");
}

async Task PartialChunkTelemetry()
{
    var bytes = RandomNumberGenerator.GetBytes(64 * 1024);
    var m = new FileManifest("slow.bin", bytes.Length, Wire.Hash(bytes));
    var sample = new TaskCompletionSource<TransferProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var dir = Path.Combine(root, "slow-body");
    await using var receiver = await TransferReceiver.StartAsync("127.0.0.1", 0, dir,
        new InlineProgress(p => { if (p.Stage == TransferStage.Receiving && p.BytesPerSecond > 0) sample.TrySetResult(p); }));
    using var client = TransferSender.CreateClient(receiver.Invitation);
    await Begin(client, m);
    var chunk = TransferSender.RequestAsync(client, $"v2/chunk?id={m.Id}&offset=0", new PausedContent(bytes, release.Task), default, m.Hash);
    try
    {
        var progress = await sample.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check(progress.Completed == 0 && !chunk.IsCompleted, "incomplete block advanced durable progress");
        Check(new FileInfo(Path.Combine(dir, Wire.PartsDirectory, m.Id + ".part")).Length == 0, "unverified bytes were committed");
    }
    finally { release.TrySetResult(); }
    await chunk;
    await TransferSender.RequestAsync(client, "v2/finish?id=" + m.Id, null, default);
    Check(await Wire.FileHashAsync(Path.Combine(dir, m.Name), default) == m.Hash, "slow-body transfer checksum differs");
    Console.WriteLine("PASS receive telemetry during partial chunk / durable progress unchanged until checksum and flush");
}

sealed class PausedContent(byte[] bytes, Task release) : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await stream.WriteAsync(bytes.AsMemory(0, 1024));
        await stream.FlushAsync();
        await Task.Delay(200);
        await stream.WriteAsync(bytes.AsMemory(1024, 1024));
        await stream.FlushAsync();
        await release;
        await stream.WriteAsync(bytes.AsMemory(2048));
    }
}

sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
{ public void Report(TransferProgress value) => report(value); }

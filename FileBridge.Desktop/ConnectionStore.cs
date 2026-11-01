using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileBridge.Core;

namespace FileBridge.Desktop;

internal sealed record ConnectionPreferences(string SendCode = "", string Address = "", string Port = "8787", string Directory = "",
    ReceiverIdentity? Identity = null, string ReverseCode = "");

internal sealed class ConnectionStore(string path)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileBridge", "connections.json");

    public ConnectionPreferences Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 65536) throw new JsonException();
            return JsonSerializer.Deserialize<ConnectionPreferences>(File.ReadAllText(path)) ?? throw new JsonException();
        }
        catch (JsonException error) { throw new InvalidDataException("保存的连接信息损坏，请重新创建连接码。", error); }
    }

    public void SavePreferences(ConnectionPreferences preferences) => Update(current => preferences with { Identity = current.Identity });

    public ReceiverIdentity GetIdentity(bool recreate = false)
    {
        var saved = Update(current =>
        {
            if (recreate || current.Identity is null) return current with { Identity = ReceiverIdentity.Create() };
            if (!current.Identity.IsUsable) throw new InvalidDataException("保存的接收凭据无效，请手动重新创建连接码。");
            return current;
        }, recover: recreate);
        return saved.Identity!;
    }

    public FileStream AcquireReceiverLease()
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try { return new FileStream(Path.Combine(EnsureDirectory(), ".receiver.lock"), options); }
        catch (IOException error) { throw new IOException("另一个窗口正在使用接收配置，请先停止该窗口的接收。", error); }
    }

    private ConnectionPreferences Update(Func<ConnectionPreferences, ConnectionPreferences> update, bool recover = false)
    {
        // Serialize read/merge/write across app instances so an old preference save cannot undo code rotation.
        using var mutex = new Mutex(false, "FileBridge.Connections." + Wire.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(path))));
        try
        {
            try { if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new IOException("连接信息正在被使用，请稍后重试。"); }
            catch (AbandonedMutexException) { }
            ConnectionPreferences current;
            try { current = Load(); }
            catch (InvalidDataException) when (recover) { current = new(); }
            var next = update(current);
            Write(next);
            return next;
        }
        finally { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } }
    }

    private void Write(ConnectionPreferences preferences)
    {
        var directory = EnsureDirectory();
        var temporary = Path.Combine(directory, ".connections-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, preferences);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
        else
        {
            System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return directory;
    }
}

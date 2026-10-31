using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileBridge.Core;

/// <summary>A selected local file and its safe path relative to the transfer root.</summary>
public sealed record TransferFile(string LocalPath, string RelativeName, long Size);
/// <summary>Authenticated peer endpoint with a certificate pin (fb2) or stable public-key pin (fb3).</summary>
public sealed record Invitation(string Url, string Token, string Pin, [property: JsonIgnore] bool PublicKeyPin = false)
{
    public string Encode() => (PublicKeyPin ? "fb3_" : "fb2_") + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Invitation Parse(string code)
    {
        code = code.Trim();
        if ((!code.StartsWith("fb2_", StringComparison.Ordinal) && !code.StartsWith("fb3_", StringComparison.Ordinal)) || code.Length > 4096)
            throw new InvalidDataException("请粘贴 FileBridge 桌面客户端生成的完整连接码（fb2_ 或 fb3_ 开头）。");
        try
        {
            var data = code[4..].Replace('-', '+').Replace('_', '/');
            data = data.PadRight((data.Length + 3) / 4 * 4, '=');
            var value = JsonSerializer.Deserialize<Invitation>(Convert.FromBase64String(data));
            if (value is null || !Uri.TryCreate(value.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != ""
                || !Wire.ValidHash(value.Pin) || !Wire.ValidHash(value.Token))
                throw new InvalidDataException("连接码中的地址或认证信息无效。");
            return value with { PublicKeyPin = code.StartsWith("fb3_", StringComparison.Ordinal) };
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new InvalidDataException("连接码不完整，请重新复制整行。", error); }
    }
}

/// <summary>File identity, size, and whole-file SHA-256 advertised by the sender.</summary>
public sealed record FileManifest(string Name, long Size, string Hash)
{
    [JsonIgnore]
    public string Id => Wire.Hash(JsonSerializer.SerializeToUtf8Bytes(new { Name, Size, Hash }));
}
/// <summary>Persisted receiving offset, prefix checksum, and completion state.</summary>
public sealed record ResumeState(long Offset, string Prefix, bool Done = false, int ParallelChunks = 1);

public static class Wire
{
    public const int ChunkSize = 4 * 1024 * 1024;
    public const int DefaultParallelChunks = 4;
    public const int MaxParallelChunks = 10;
    public const string PartsDirectory = ".filebridge-parts";
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string CertificatePin(X509Certificate2 certificate, bool publicKey) =>
        publicKey ? Hash(certificate.PublicKey.ExportSubjectPublicKeyInfo()) : Hash(certificate.RawData);
    public static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static bool ValidName(string? name) => name is { Length: > 0 and <= 4096 } && !name.Any(c => "\\:<>\"|?*".Contains(c))
        && !name.Any(char.IsControl) && !Path.IsPathRooted(name)
        && name.Split('/').All(part => part.Length > 0 && part is not "." and not ".."
            && !part.EndsWith('.') && !part.EndsWith(' ') && !WindowsDeviceName(part)
            && !part.Equals(PartsDirectory, StringComparison.OrdinalIgnoreCase));
    private static bool WindowsDeviceName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$"
            || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && "123456789¹²³".Contains(stem[3]);
    }
    public static async Task<string> FileHashAsync(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
    /// <summary>Prefer an active Tailscale IPv4, then a private LAN IPv4, then loopback.</summary>
    public static string DiscoverAddress() => NetworkAddressDiscovery.DiscoverAddress();
}

public sealed class TransferException(int status, string message, Exception? inner = null) : Exception(message, inner)
{ public int Status { get; } = status; }


using System.Collections;
using System.Globalization;
using System.Resources;

namespace FileBridge.Desktop;

internal sealed class Localization
{
    private static readonly ResourceManager Resources = new("FileBridge.Desktop.Resources.Strings", typeof(Localization).Assembly);
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh");
    private static readonly Lazy<Localization> Shared = new(() => new());
    private readonly string preferenceFile;
    private readonly Dictionary<string, string> coreMessages;
    private readonly KeyValuePair<string, string>[] corePrefixes;

    public static Localization Current => Shared.Value;
    public string Language { get; private set; }
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public IReadOnlyList<string> Keys { get; }

    public Localization(string? preferenceFile = null, CultureInfo? systemCulture = null)
    {
        this.preferenceFile = preferenceFile ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileBridge", "language.txt");
        Language = (systemCulture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "zh" ? "zh" : "en";
        try
        {
            var saved = File.ReadAllText(this.preferenceFile).Trim();
            if (saved is "zh" or "en") Language = saved;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Keys = Resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!.Cast<DictionaryEntry>()
            .Select(entry => (string)entry.Key).Order().ToArray();
        coreMessages = Keys.Where(key => key.StartsWith("Core.", StringComparison.Ordinal))
            .ToDictionary(key => Resources.GetString(key, Chinese)!, key => key, StringComparer.Ordinal);
        corePrefixes = Keys.Where(key => key.StartsWith("CorePrefix.", StringComparison.Ordinal))
            .Select(key => new KeyValuePair<string, string>(Resources.GetString(key, Chinese)!, key))
            .OrderByDescending(pair => pair.Key.Length).ToArray();
    }

    public string Text(string key, params object[] args)
    {
        var template = Resources.GetString(key, Culture) ?? throw new InvalidOperationException("Missing translation: " + key);
        return args.Length == 0 ? template : string.Format(Culture, template, args);
    }

    public bool SetLanguage(string language)
    {
        if (language is not ("zh" or "en")) throw new ArgumentException("Unsupported language.", nameof(language));
        Language = language;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(preferenceFile))!);
            File.WriteAllText(preferenceFile, language);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // Wire errors stay canonical for older peers; only their presentation is localized.
    public string CoreText(string text)
    {
        if (Language == "zh") return text;
        if (coreMessages.TryGetValue(text, out var key)) return Text(key);
        foreach (var prefix in corePrefixes)
        {
            if (!text.StartsWith(prefix.Key, StringComparison.Ordinal)) continue;
            var suffix = text[prefix.Key.Length..];
            return Text(prefix.Value) + (prefix.Value == "CorePrefix.ReceiveError" ? CoreText(suffix) : suffix);
        }
        return text;
    }

    internal IReadOnlyList<string> ResourceKeys(string language) => Resources.GetResourceSet(
        language == "en" ? CultureInfo.InvariantCulture : Chinese, true, false)!.Cast<DictionaryEntry>()
        .Select(entry => (string)entry.Key).Order().ToArray();
}

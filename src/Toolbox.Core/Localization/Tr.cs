using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Toolbox.Core.Localization;

/// <summary>
/// Translation of the user interface. The code holds the English text; a language file per project
/// (<c>Locales/it.json</c>, embedded, <c>{ "English text": "Testo italiano" }</c>) holds the translation. A text with no entry is
/// shown as written, so a missing translation is never an error. Use <c>T("Text")</c> or, for values,
/// <c>T("Loaded {0} items", count)</c>.
/// The language is chosen once per app (see <see cref="Initialize"/>) and changed with <see cref="SetLanguage"/>; the page is reloaded
/// after a change, so no component needs to listen for it.
/// </summary>
public static class Tr
{
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[] { ("en", "English"), ("it", "Italiano") };

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, string>> Dictionaries = new();
    private static readonly HashSet<Assembly> Loaded = new();
    private static string _language = "en";
    private static string? _settingsFile;

    public static string Language => _language;

    /// <summary>Reads the saved language of this app, or picks Italian when the Windows language is Italian and English otherwise.</summary>
    public static void Initialize(string appFolderName)
    {
        lock (Gate)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appFolderName);
            _settingsFile = Path.Combine(dir, "language.txt");

            string? saved = null;
            try { if (File.Exists(_settingsFile)) saved = File.ReadAllText(_settingsFile).Trim(); } catch { /* defaults below */ }

            _language = Languages.Any(l => l.Code == saved)
                ? saved!
                : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en";
        }
    }

    /// <summary>Loads the <c>Locales/&lt;code&gt;.json</c> files embedded in an assembly (safe to call more than once).</summary>
    public static void RegisterAssembly(Assembly assembly)
    {
        lock (Gate)
        {
            if (!Loaded.Add(assembly)) return;

            foreach (var resource in assembly.GetManifestResourceNames())
            {
                var marker = ".Locales.";
                var at = resource.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0 || !resource.EndsWith(".json", StringComparison.Ordinal)) continue;

                var code = resource[(at + marker.Length)..^".json".Length];
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) continue;

                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
                if (!Dictionaries.TryGetValue(code, out var target)) Dictionaries[code] = target = new();
                foreach (var (english, translated) in entries) target.TryAdd(english, translated);
            }
        }
    }

    public static void SetLanguage(string code)
    {
        if (Languages.All(l => l.Code != code)) return;

        lock (Gate)
        {
            _language = code;
            try
            {
                if (_settingsFile is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
                    File.WriteAllText(_settingsFile, code);
                }
            }
            catch { /* the choice still holds for this run */ }
        }
    }

    public static string T(string english)
    {
        if (_language == "en") return english;
        lock (Gate)
        {
            return Dictionaries.TryGetValue(_language, out var map) && map.TryGetValue(english, out var translated) ? translated : english;
        }
    }

    public static string T(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);
}

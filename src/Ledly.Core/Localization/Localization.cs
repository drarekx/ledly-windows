using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Ledly;

/// <summary>
/// Translated UI text. The key is the Spanish text; <c>web/i18n/en.json</c> and <c>web/i18n/ca.json</c>
/// (embedded, shared with the web UI) map it to English and Catalan. Formats use .NET placeholders
/// ({0}, {1}) in every language.
/// </summary>
public static class L
{
    static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
    {
        ["en"] = LoadTable("en"),
        ["ca"] = LoadTable("ca"),
    };

    static Dictionary<string, string> LoadTable(string code)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"web/i18n/{code}.json");
            if (stream is null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>The language actually used: the chosen one, or the system's if supported (English otherwise).</summary>
    public static AppLanguage Current
    {
        get
        {
            var choice = SettingsOrNull()?.Language ?? AppLanguage.system;
            if (choice != AppLanguage.system) return choice;
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
            {
                "es" => AppLanguage.es,
                "ca" => AppLanguage.ca,
                _ => AppLanguage.en,
            };
        }
    }

    static Settings? SettingsOrNull()
    {
        try { return Settings.Shared; } catch { return null; }
    }

    public static string Tr(string key) => Translate(key, Current);

    public static string Tr(string key, params object?[] args) =>
        string.Format(CultureInfo.InvariantCulture, Tr(key), args);

    /// <summary>English text for <paramref name="key"/> whatever the UI language (names other apps see, like WLED effects).</summary>
    public static string TrEnglish(string key) => Translate(key, AppLanguage.en);

    public static string Translate(string key, AppLanguage language) => language switch
    {
        AppLanguage.en => Tables["en"].GetValueOrDefault(key, key),
        AppLanguage.ca => Tables["ca"].GetValueOrDefault(key, key),
        _ => key,
    };
}

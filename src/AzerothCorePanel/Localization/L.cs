using System.ComponentModel;
using System.Globalization;

namespace AzerothCorePanel.Localization;

/// <summary>Panel languages. The order matches the columns of <see cref="Strings"/>.</summary>
public enum Language
{
    Portuguese = 0,
    English = 1,
    Spanish = 2,
}

/// <summary>
/// Current language and translated texts. XAML binds to the indexer through <see cref="TrExtension"/>, so the
/// window changes language at once when <see cref="Set"/> is called; code uses <see cref="T"/> and <see cref="F"/>.
/// </summary>
public sealed class L : INotifyPropertyChanged
{
    private static readonly CultureInfo[] Cultures =
    {
        CultureInfo.GetCultureInfo("pt-BR"),
        CultureInfo.GetCultureInfo("en-US"),
        CultureInfo.GetCultureInfo("es-ES"),
    };

    private L()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed.</summary>
    public static event Action? Changed;

    public static L Instance { get; } = new();

    public static Language Current { get; private set; } = Language.English;

    /// <summary>Culture of the current language, used for numbers and dates.</summary>
    public static CultureInfo Culture => Cultures[(int)Current];

    /// <summary>Bound from XAML: "[Key]".</summary>
    public string this[string key] => T(key);

    /// <summary>Text for <paramref name="key"/>; falls back to Portuguese, then to the key itself.</summary>
    public static string T(string key)
    {
        if (!Strings.Table.TryGetValue(key, out string[]? texts))
        {
            return key;
        }

        string text = texts[(int)Current];
        return text.Length > 0 ? text : texts[0];
    }

    /// <summary>Formatted text: <c>string.Format</c> with the current culture.</summary>
    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

    /// <summary>"[Painel] message" in the current language, for log lines written by the panel.</summary>
    public static string Log(string message) => $"{T("Log.Prefix")} {message}";

    public static void Set(Language language)
    {
        Current = language;

        // Numbers and dates written from code follow the panel language.
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentCulture = Culture;
        CultureInfo.CurrentUICulture = Culture;

        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    /// <summary>"pt", "en" or "es"; anything else (or empty) is English, the default language.</summary>
    public static Language FromCode(string? code) => (code ?? string.Empty).ToLowerInvariant() switch
    {
        "pt" => Language.Portuguese,
        "es" => Language.Spanish,
        _ => Language.English,
    };

    public static string Code(Language language) => language switch
    {
        Language.Portuguese => "pt",
        Language.Spanish => "es",
        _ => "en",
    };
}

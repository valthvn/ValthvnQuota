using System;
using System.Globalization;

namespace ValthvnQuota;

/// <summary>
/// Manages application-wide language selection (English default, French optional)
/// and persistence under HKCU\Software\ValthvnQuota.
/// </summary>
public static class LanguageSettings
{
    private const string KeyPath = @"Software\ValthvnQuota";
    private const string LanguageValueName = "Language";

    public const string English = "en";
    public const string French = "fr";

    public static event Action? Changed;

    /// <summary>
    /// Gets or sets the application language code ("en" or "fr"). Default is "en".
    /// </summary>
    public static string CurrentLanguage
    {
        get => Normalize(RegistrySettings.ReadString(KeyPath, LanguageValueName, English));
        set
        {
            string normalized = Normalize(value);
            if (string.Equals(CurrentLanguage, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            RegistrySettings.WriteString(KeyPath, LanguageValueName, normalized);
            ApplyLanguage();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The CultureInfo corresponding to the selected language.
    /// </summary>
    public static CultureInfo CurrentCulture => CurrentLanguage == French
        ? CultureInfo.GetCultureInfo("fr-FR")
        : CultureInfo.GetCultureInfo("en-US");

    /// <summary>
    /// Normalizes input language string to "en" or "fr". Defaults to "en".
    /// </summary>
    public static string Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
            return English;

        lang = lang.Trim().ToLowerInvariant();
        if (lang.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
            return French;

        return English;
    }

    /// <summary>
    /// Synchronizes thread and process UI cultures with the selected language and updates AppStrings.
    /// </summary>
    public static void ApplyLanguage()
    {
        var culture = CurrentCulture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        AppStrings.RefreshLanguage();
    }
}

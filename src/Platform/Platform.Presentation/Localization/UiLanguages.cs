namespace Platform.Presentation.Localization;

/// <summary>A screen language the desktop offers (FIX-13b); the name is written in the language itself, so everyone finds their own.</summary>
public sealed record UiLanguage(string Code, string NativeName);

/// <summary>The screen languages with translations (FIX-13): English and Arabic.</summary>
public static class UiLanguages
{
    public static IReadOnlyList<UiLanguage> All { get; } = [new("en", "English"), new("ar", "العربية")];

    /// <summary>The offered language of a culture name ("ar-EG" -> Arabic), or null when it is not one of them.</summary>
    public static UiLanguage? Find(string? cultureName)
        => string.IsNullOrWhiteSpace(cultureName)
            ? null
            : All.FirstOrDefault(l => string.Equals(l.Code, cultureName.Trim().Split('-')[0], StringComparison.OrdinalIgnoreCase));
}

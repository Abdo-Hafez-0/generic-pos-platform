using System.Globalization;
using Platform.Presentation.Localization;

namespace UI.Tests;

/// <summary>Tests that change the process culture run alone (no parallel test reads resources meanwhile) and restore it.</summary>
[CollectionDefinition(nameof(ProcessCulture), DisableParallelization = true)]
public sealed class ProcessCulture;

/// <summary>FIX-01 decision 4: the display language comes from configuration; an unknown value never stops the application.</summary>
[Collection(nameof(ProcessCulture))]
public sealed class UiCultureTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xx-not-a-language")]
    public void Nothing_or_something_unknown_falls_back_to_English(string? configured)
        => Assert.Equal(UiCulture.Default, UiCulture.Resolve(configured).Name);

    [Fact]
    public void Arabic_is_right_to_left_and_English_is_not()
    {
        UiCulture.Apply("ar-EG");
        Assert.True(UiCulture.IsRightToLeft);
        Assert.Equal("ar-EG", CultureInfo.DefaultThreadCurrentUICulture?.Name);

        UiCulture.Apply("en");
        Assert.False(UiCulture.IsRightToLeft);
    }

    [Theory]
    [InlineData("ar", "ar")]
    [InlineData("ar-EG", "ar")]
    [InlineData("EN", "en")]
    [InlineData("fr", null)]
    [InlineData(null, null)]
    public void The_offered_languages_are_found_by_culture_name(string? culture, string? expected)
        => Assert.Equal(expected, UiLanguages.Find(culture)?.Code);

    [Fact]
    public async Task A_language_chosen_inside_an_async_method_stays_for_screens_opened_later()
    {
        // FIX-13b (found in the real executable): .NET restores the thread culture when an async method that changed it returns, so a
        // screen opened afterwards read English again. The display language lives in UiCulture.Current and in every resource class.
        static async Task ChooseArabicAsync()
        {
            await Task.Yield();
            UiCulture.Apply("ar");
        }

        UiCulture.Apply("en");
        await ChooseArabicAsync();

        Assert.Equal("ar", UiCulture.Current.Name);
        Assert.True(UiCulture.IsRightToLeft);
        Assert.Equal("ar", POS.UI.Resources.PosText.Culture?.Name);          // a module screen's texts
        Assert.Equal("ar", Client.Desktop.Resources.ShellText.Culture?.Name); // the shell's texts
        UiCulture.Apply("en");
        Assert.Equal("en", POS.UI.Resources.PosText.Culture?.Name);
    }

    [Fact]
    public void The_screen_language_does_not_change_how_numbers_are_written()
    {
        UiCulture.ApplyFormatting("en");
        UiCulture.Apply("ar");
        Assert.Equal("2.50", 2.5m.ToString("0.00", CultureInfo.CurrentCulture));
        UiCulture.Apply("en");
    }
}

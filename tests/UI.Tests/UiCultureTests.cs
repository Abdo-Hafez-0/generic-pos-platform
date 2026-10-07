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
}

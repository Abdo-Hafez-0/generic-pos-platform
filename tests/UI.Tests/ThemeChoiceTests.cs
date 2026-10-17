using System.Windows;
using System.Windows.Media;
using Client.Desktop.Themes;

namespace UI.Tests;

/// <summary>
/// MISS-08d (owner's decisions): each user chooses Like Windows, Light or Dark; before anyone signs in the colours follow Windows. A choice
/// is applied by replacing the colour set, and everything that names its colours (DynamicResource) follows at once.
/// </summary>
public sealed class ThemeChoiceTests
{
    [Theory]
    [InlineData(null, false, ThemeKind.Light)]
    [InlineData(null, true, ThemeKind.Dark)]      // like Windows
    [InlineData("light", true, ThemeKind.Light)]  // the user's choice wins over Windows
    [InlineData("dark", false, ThemeKind.Dark)]
    [InlineData(" Dark ", false, ThemeKind.Dark)]
    [InlineData("something", true, ThemeKind.Dark)]   // an unknown value follows Windows
    public void A_choice_and_the_windows_setting_give_the_colours(string? choice, bool windowsIsDark, ThemeKind expected)
        => Assert.Equal(expected, ThemeManager.Resolve(choice, windowsIsDark));

    [Fact]
    public void Switching_the_colour_set_restyles_what_names_its_colours_and_keeps_the_control_styles()
    {
        WpfThread.Run(() =>
        {
            var resources = new ResourceDictionary();
            var box = new System.Windows.Controls.Border { Resources = resources };
            box.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Brush.Page");
            try
            {
                ThemeManager.Apply(resources, ThemeKind.Light);
                var light = ((SolidColorBrush)box.Background).Color;
                Assert.Equal(2, resources.MergedDictionaries.Count);   // colours, then the control styles

                ThemeManager.Apply(resources, ThemeKind.Dark);
                var dark = ((SolidColorBrush)box.Background).Color;

                Assert.Equal(2, resources.MergedDictionaries.Count);
                Assert.True(dark.R + dark.G + dark.B < light.R + light.G + light.B, $"the dark page ({dark}) is not darker than the light one ({light})");
                Assert.True(resources.Contains("Button.Primary"));   // the control styles stayed
            }
            finally
            {
                ThemeManager.Apply(Application.Current.Resources, ThemeKind.Light);   // the shared test application stays light
            }
        });
    }
}

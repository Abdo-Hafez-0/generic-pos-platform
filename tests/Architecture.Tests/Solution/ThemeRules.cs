using System.Text.RegularExpressions;

namespace Architecture.Tests.Solution;

/// <summary>
/// MISS-08: the Matjar theme. ARCH-UI-001 .. ARCH-UI-002.
///   Screens and windows name theme colours (DynamicResource Brush.*), never a colour of their own, so the light and dark themes restyle
///   everything and a new screen cannot break the dark theme. The two colour sets define the same names.
/// </summary>
public sealed class ThemeRules
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> Views() => Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.xaml", SearchOption.AllDirectories)
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}"));

    [Fact(DisplayName = "ARCH-UI-001: No screen or window writes a colour; colours come from the theme by name (DynamicResource)")]
    public void ViewsUseThemeColoursOnly()
    {
        var colour = new Regex(@"=""(#[0-9A-Fa-f]{3,8}|(?:White|Black|Red|Green|Blue|Gray|Grey|Yellow|Orange|LightGray|DarkGray|Silver|WhiteSmoke)"")",
            RegexOptions.CultureInvariant);
        var staticBrush = new Regex(@"\{StaticResource Brush\.", RegexOptions.CultureInvariant);   // a theme colour must follow a theme switch
        var offenders = new List<string>();
        foreach (var view in Views())
        {
            var lines = File.ReadAllLines(view);
            for (var i = 0; i < lines.Length; i++)
                if (colour.IsMatch(lines[i]) || staticBrush.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(RepoRoot(), view)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(offenders.Count == 0, "Colours written in views:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
        Assert.True(Views().Count() >= 25);
    }

    [Fact(DisplayName = "ARCH-UI-002: The light and dark colour sets define the same names, and every name a view uses exists")]
    public void BothThemesDefineTheSameNames()
    {
        var themes = Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "Themes");
        static HashSet<string> Keys(string file) => Regex.Matches(File.ReadAllText(file), @"x:Key=""(Brush\.[A-Za-z]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        var light = Keys(Path.Combine(themes, "Light.xaml"));
        var dark = Keys(Path.Combine(themes, "Dark.xaml"));

        Assert.NotEmpty(light);
        Assert.Equal(light.Order(), dark.Order());

        var used = Views().Append(Path.Combine(themes, "Controls.xaml"))
            .SelectMany(v => Regex.Matches(File.ReadAllText(v), @"DynamicResource (Brush\.[A-Za-z]+)").Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.Empty(used.Except(light));
    }
}

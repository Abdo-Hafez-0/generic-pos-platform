using System.Text.RegularExpressions;
using System.Xml.Linq;
using Platform.Presentation.Localization;

namespace UI.Tests;

/// <summary>
/// FIX-13c: every screen text exists in Arabic. Each resource file has an Arabic twin (*.ar.resx) with exactly the same keys and the same
/// placeholders ({0}, {1:N2} ...), so a text added later cannot stay English unnoticed and a translation can never break a format.
/// </summary>
[Collection(nameof(ProcessCulture))]
public sealed partial class TranslationTests
{
    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static IReadOnlyDictionary<string, string> Strings(string path)
        => XDocument.Load(path).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    public static TheoryData<string> NeutralResourceFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.resx", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.EndsWith(".ar.resx", StringComparison.Ordinal)))
            data.Add(Path.GetRelativePath(RepoRoot(), file));
        return data;
    }

    [Theory]
    [MemberData(nameof(NeutralResourceFiles))]
    public void Every_text_has_an_Arabic_translation_with_the_same_placeholders(string neutralFile)
    {
        var neutral = Strings(Path.Combine(RepoRoot(), neutralFile));
        var arabicFile = Path.Combine(RepoRoot(), neutralFile[..^".resx".Length] + ".ar.resx");
        Assert.True(File.Exists(arabicFile), $"{neutralFile} has no Arabic twin");
        var arabic = Strings(arabicFile);

        Assert.Equal(neutral.Keys.Order(), arabic.Keys.Order());
        foreach (var (key, english) in neutral)
        {
            Assert.Equal(Placeholder().Matches(english).Select(m => m.Value).Order(), Placeholder().Matches(arabic[key]).Select(m => m.Value).Order());
            Assert.False(string.IsNullOrWhiteSpace(arabic[key]) && !string.IsNullOrWhiteSpace(english), $"{neutralFile}: {key} is empty in Arabic");
        }
    }

    [Fact]
    public void Choosing_Arabic_shows_the_Arabic_texts_of_the_shell_and_the_modules()
    {
        try
        {
            UiCulture.Apply("ar");
            Assert.Equal("نقطة البيع", POS.UI.Resources.PosText.ScreenTitle);
            Assert.Equal("تسجيل الخروج", Client.Desktop.Resources.ShellText.SignOut);
            Assert.Equal("المبيعات", Platform.Presentation.Resources.PresentationText.GroupSales);
            Assert.Equal("تم البيع: 9.00.", string.Format(System.Globalization.CultureInfo.InvariantCulture, POS.UI.Resources.PosText.SaleCompleted, 9m));

            UiCulture.Apply("en");
            Assert.Equal("Point of sale", POS.UI.Resources.PosText.ScreenTitle);
        }
        finally
        {
            UiCulture.Apply("en");
        }
    }
}

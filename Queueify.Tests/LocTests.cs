using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Queueify.Services;

namespace Queueify.Tests;

public partial class LocTests
{
    [Fact]
    public void BothLanguages_HaveTheSameKeys()
    {
        Assert.Empty(Loc.English.Keys.Except(Loc.Spanish.Keys));
        Assert.Empty(Loc.Spanish.Keys.Except(Loc.English.Keys));
    }

    [Fact]
    public void Translations_KeepTheSamePlaceholders()
    {
        foreach (var (key, english) in Loc.English)
        {
            var expected = Placeholders(english);
            Assert.True(expected.SetEquals(Placeholders(Loc.Spanish[key])), $"placeholders differ in \"{key}\"");
        }
    }

    [Fact]
    public void NoTranslation_IsEmpty()
    {
        Assert.All(Loc.English.Concat(Loc.Spanish), pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
    }

    [Fact]
    public void EveryKeyTheAppUses_Exists()
    {
        var root = RepoRoot();
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains("Queueify.Tests")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in CodeKey().Matches(text)) used.Add(m.Groups[1].Value);
            foreach (Match m in XamlKey().Matches(text)) used.Add(m.Groups[1].Value);
        }

        Assert.NotEmpty(used);
        Assert.Empty(used.Where(k => !Loc.English.ContainsKey(k)));
    }

    [Theory]
    [InlineData("system", "es-AR", "es")]
    [InlineData("system", "es-ES", "es")]
    [InlineData("system", "en-US", "en")]
    [InlineData("system", "fr-FR", "en")]   // not translated: English
    [InlineData("es", "en-US", "es")]
    [InlineData("en", "es-AR", "en")]
    [InlineData("??", "es-AR", "es")]       // unknown setting: like "system"
    public void Resolve_PicksTheAppLanguage(string preference, string windows, string expected)
    {
        Assert.Equal(expected, Loc.Resolve(preference, CultureInfo.GetCultureInfo(windows)));
    }

    [Theory]
    [InlineData("es", "es-AR", "es-AR")]    // Windows already speaks it: keep its regional formats
    [InlineData("es", "en-US", "es-AR")]
    [InlineData("en", "en-GB", "en-GB")]
    [InlineData("en", "es-AR", "en-US")]
    public void CultureFor_FormatsDatesAndNumbersToMatch(string language, string windows, string expected)
    {
        Assert.Equal(expected, Loc.CultureFor(language, CultureInfo.GetCultureInfo(windows)).Name);
    }

    [Fact]
    public void T_UsesTheCurrentLanguage_AndFallsBackToTheKey()
    {
        using (Loc.Override("es"))
            Assert.Equal(Loc.Spanish["nav.queue"], Loc.T("nav.queue"));
        using (Loc.Override("en"))
        {
            Assert.Equal(Loc.English["nav.queue"], Loc.T("nav.queue"));
            Assert.Equal("no.such.key", Loc.T("no.such.key"));
        }
    }

    [Fact]
    public void QueueSummaries_AreTranslated()
    {
        using (Loc.Override("es"))
        {
            Assert.Equal("1 álbum", QueueText.Albums(1));
            Assert.Equal("3 álbumes", QueueText.Albums(3));
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).ToHashSet();

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here))!;

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"Loc\.[TF]\(""([a-z0-9_.]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{l:Tr Key=([a-z0-9_.]+)\}")]
    private static partial Regex XamlKey();
}

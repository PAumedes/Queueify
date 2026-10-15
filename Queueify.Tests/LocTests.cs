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
    public void TheKeyScan_ReadsACheckoutThatLivesUnderADotClaudeFolder_ButNotWorktreesNestedInIt()
    {
        var s = Path.DirectorySeparatorChar;
        var root = string.Join(s, "C:", "r", ".claude", "worktrees", "w");
        Assert.True(LocTests.IsAppSource(root, string.Join(s, root, "Queueify.Web", "Components", "X.razor")));
        Assert.False(LocTests.IsAppSource(root, string.Join(s, root, ".claude", "worktrees", "v", "Queueify.Web", "X.razor")));
        Assert.False(LocTests.IsAppSource(root, string.Join(s, root, "Queueify.Web", "obj", "X.cs")));
    }

    [Fact]
    public void EveryKeyTheAppUses_Exists()
    {
        var root = RepoRoot();
        var used = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(f => IsAppSource(root, f)))
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
    [InlineData("es", "es-AR", "es-AR")]   // Argentina: formats from the region, whatever language Windows shows
    [InlineData("es", "en-US", "es-AR")]   // the region speaks another language: Argentine formats
    [InlineData("en", "en-AU", "en-AU")]
    [InlineData("en", "es-AR", "en-US")]
    public void CultureFor_KeepsTheRegionalFormatWhenItSpeaksTheAppLanguage(string language, string region, string expected)
    {
        Assert.Equal(expected, Loc.CultureFor(language, CultureInfo.GetCultureInfo(region)).Name);
    }

    [Fact]
    public void SpanishNumbers_UseADecimalComma()
    {
        using (Loc.Override("es"))
        {
            var items = new[] { Rated(5), Rated(4) };
            Assert.Contains("promedio 4,5 ★", QueueText.ListenedSummary(items));
        }
    }

    [Fact]
    public void SpanishSummaries_AgreeInTheSingular()
    {
        using (Loc.Override("es"))
        {
            Assert.Equal("1 álbum escuchado", QueueText.ListenedSummary([Rated(-1)]));
            var now = DateTime.Now;
            var pending = new[] { new Queueify.Models.BacklogItem { Album = "x", AddedAt = now } };
            Assert.Equal("1 álbum esperando  ·  1 agregado esta semana", QueueText.PendingSummary(pending, now));
        }
    }

    [Fact]
    public void KeysPickedThroughConditions_ExistToo()
    {
        // Keys chosen with ?: or passed along (Loc.T(listened ? "a" : "b"), SetBadge(..., "badge.x")) don't
        // follow Loc.T("…"); every quoted string shaped like a key of a known group must exist.
        var groups = Loc.English.Keys.Select(k => k.Split('.')[0]).ToHashSet();
        var unknown = new List<string>();
        foreach (var file in SourceFiles(".cs"))
            foreach (Match m in KeyLike().Matches(File.ReadAllText(file)))
                if (groups.Contains(m.Groups[1].Value.Split('.')[0]) && !Loc.English.ContainsKey(m.Groups[1].Value) && !FileName().IsMatch(m.Groups[1].Value))
                    unknown.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        Assert.Empty(unknown);
    }

    [Fact]
    public void NoKeyIsDefinedTwice()
    {
        // The tables use index initializers, where a second ["key"] silently replaces the first.
        foreach (var table in new[] { "Strings.En.cs", "Strings.Es.cs" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "Queueify.Core", "Services", table));
            var keys = TableKey().Matches(text).Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(Loc.English.Count, keys.Count);   // the scan really saw the whole table
            Assert.Empty(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => $"{table}: {g.Key}"));
        }
    }

    [Fact]
    public void BadgeNames_HaveTheirSingular()
    {
        // SetBadge adds ".one" for a count of 1 ("Escuchados, 1 escuchado").
        var badges = Loc.English.Keys.Where(k => k.StartsWith("badge.") && !k.EndsWith(".one")).ToList();
        Assert.NotEmpty(badges);
        Assert.All(badges, k => Assert.True(Loc.English.ContainsKey(k + ".one") && Loc.Spanish.ContainsKey(k + ".one"), k));
    }

    [Fact]
    public void KeyScan_SeesKeysPassedAlong()
    {
        var seen = SourceFiles(".cs").SelectMany(f => KeyLike().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet();
        Assert.Contains("badge.waiting", seen);
        Assert.Contains("toast.moved_back", seen);
    }
    private static Queueify.Models.BacklogItem Rated(double rating) =>
        new() { Album = "x", AddedAt = DateTime.Now.AddDays(-30), ListenedAt = DateTime.Now, Rating = rating };

    private static IEnumerable<string> SourceFiles(string extension) =>
        Directory.EnumerateFiles(RepoRoot(), "*" + extension, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains("Queueify.Tests") && !f.Contains("Queueify.Web.Tests") && !f.EndsWith("Strings.En.cs") && !f.EndsWith("Strings.Es.cs"));

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

    // Only the numbers: a date format may differ between languages ("3 Oct 2026", "3 de oct. de 2026").
    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>Source the string-key scan reads. Judged on the path below the root, so a checkout that itself lives under .claude (an agent worktree) still counts.</summary>
    internal static bool IsAppSource(string root, string file)
    {
        var s = Path.DirectorySeparatorChar;
        var rel = s + Path.GetRelativePath(root, file);   // leading separator, so a top-level obj or .claude folder matches too
        return (rel.EndsWith(".cs") || rel.EndsWith(".xaml") || rel.EndsWith(".razor"))
            && !rel.Contains($"{s}obj{s}") && !rel.Contains($"{s}bin{s}") && !rel.Contains("Queueify.Tests") && !rel.Contains("Queueify.Web.Tests")
            && !rel.Contains($"{s}.claude{s}");
    }

    private static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here))!;

    [GeneratedRegex(@"\{(\d+)(:[^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"Loc\.[TF]\(""([A-Za-z0-9_.]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{l:Tr Key=([A-Za-z0-9_.]+)\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"""([a-zA-Z]+\.[A-Za-z0-9_.]+)""")]
    private static partial Regex KeyLike();

    [GeneratedRegex(@"\[""([A-Za-z0-9_.]+)""\] =")]
    private static partial Regex TableKey();

    [GeneratedRegex(@"\.(json|db|rules|log|tmp|exe|ico|svg|png|pri|xbf)$")]
    private static partial Regex FileName();
}

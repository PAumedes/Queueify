using Queueify.Services;

namespace Queueify.Tests;

public class ShortcutsTests
{
    [Fact]
    public void The_web_list_has_only_keys_the_web_app_handles()
    {
        var keys = Shortcuts.Web.Select(s => s.Keys).ToList();
        Assert.DoesNotContain("Ctrl+B", keys);   // the desktop's side panel
        Assert.Contains("Ctrl+K", keys);
        Assert.Contains("Ctrl+F", keys);
        Assert.Contains("F1", keys);
    }

    [Fact]
    public void The_web_list_describes_Ctrl_K_for_the_web()
    {
        using (Loc.Override("en"))
            Assert.DoesNotContain("Ctrl+N", Shortcuts.Web.Single(s => s.Keys == "Ctrl+K").Description);
    }
}
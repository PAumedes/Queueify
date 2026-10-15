using Microsoft.AspNetCore.Identity;
using Queueify.Services;
using Queueify.Web.Services;

namespace Queueify.Web.Tests;

public sealed class PasswordRulesTests
{
    [Fact]
    public void The_default_rules_are_listed_in_english()
    {
        using var _ = Loc.Override("en");
        Assert.Equal("At least 6 characters, including an uppercase letter, a lowercase letter, a digit and a symbol.", PasswordRules.Hint(new PasswordOptions()));
    }

    [Fact]
    public void The_default_rules_are_listed_in_spanish()
    {
        using var _ = Loc.Override("es");
        Assert.Equal("Al menos 6 caracteres, con una mayúscula, una minúscula, un dígito y un símbolo.", PasswordRules.Hint(new PasswordOptions()));
    }

    [Fact]
    public void Only_the_rules_the_server_asks_for_are_listed()
    {
        using var _ = Loc.Override("en");
        var options = new PasswordOptions { RequiredLength = 12, RequireUppercase = false, RequireLowercase = false, RequireNonAlphanumeric = false };
        Assert.Equal("At least 12 characters, including a digit.", PasswordRules.Hint(options));
    }

    [Fact]
    public void With_nothing_else_asked_only_the_length_is_given()
    {
        using var _ = Loc.Override("en");
        var options = new PasswordOptions { RequireDigit = false, RequireUppercase = false, RequireLowercase = false, RequireNonAlphanumeric = false };
        Assert.Equal("At least 6 characters.", PasswordRules.Hint(options));
    }

    [Fact]
    public async Task The_register_page_shows_the_rules_before_anything_is_typed()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            var root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            b.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(root, "app.db")}");
            b.UseSetting("Queueify:DataDir", Path.Combine(root, "users"));
        });
        var html = await factory.CreateClient().GetStringAsync("/Account/Register", TestContext.Current.CancellationToken);
        Assert.Contains("At least 6 characters, including an uppercase letter", html);
    }
}

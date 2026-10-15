using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Queueify.Services;
using Queueify.Web.Services;

namespace Queueify.Web.Tests;

public sealed partial class PasswordRulesTests
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

    private const string Rules = "At least 6 characters, including an uppercase letter";

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            var root = Path.Combine(Path.GetTempPath(), "qw-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            b.UseSetting("ConnectionStrings:DefaultConnection", $"DataSource={Path.Combine(root, "app.db")}");
            b.UseSetting("Queueify:DataDir", Path.Combine(root, "users"));
        });

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex Token();

    [Fact]
    public async Task The_register_page_shows_the_rules_before_anything_is_typed()
    {
        using var factory = Factory();
        var html = await factory.CreateClient().GetStringAsync("/Account/Register", TestContext.Current.CancellationToken);
        Assert.Contains(Rules, html);
    }

    [Fact]
    public async Task The_rules_shown_follow_what_the_server_is_set_to_enforce()
    {
        using var factory = Factory().WithWebHostBuilder(b => b.ConfigureServices(s => s.Configure<IdentityOptions>(o => { o.Password.RequiredLength = 12; o.Password.RequireNonAlphanumeric = false; })));
        var html = await factory.CreateClient().GetStringAsync("/Account/Register", TestContext.Current.CancellationToken);
        Assert.Contains("At least 12 characters, including an uppercase letter, a lowercase letter and a digit.", html);
    }

    [Fact]
    public async Task Every_page_where_a_password_is_chosen_shows_the_rules()
    {
        using var factory = Factory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var token = Token().Match(await client.GetStringAsync("/Account/Register", TestContext.Current.CancellationToken)).Groups[1].Value;
        var signUp = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "register", ["Input.Email"] = "owner@example.com",
            ["Input.Password"] = "Passw0rd!x", ["Input.ConfirmPassword"] = "Passw0rd!x",
        }), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, signUp.StatusCode);

        foreach (var path in new[] { "/Account/Manage/ChangePassword", "/Account/ResetPassword?code=abc" })
            Assert.Contains(Rules, await client.GetStringAsync(path, TestContext.Current.CancellationToken));
    }
}

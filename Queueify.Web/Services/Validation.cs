using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Queueify.Services;

namespace Queueify.Web.Services;

// Form messages that DataAnnotations would write in English: the same rules, said in the user's language.

public sealed class LocRequiredAttribute : RequiredAttribute
{
    public override string FormatErrorMessage(string name) => Loc.T("web.val.required");
}

public sealed class LocEmailAttribute : ValidationAttribute
{
    private static readonly EmailAddressAttribute Rule = new();
    public override bool IsValid(object? value) => Rule.IsValid(value);
    public override string FormatErrorMessage(string name) => Loc.T("web.val.email");
}

public sealed class LocStringLengthAttribute(int maximum) : StringLengthAttribute(maximum)
{
    public override string FormatErrorMessage(string name) => Loc.F("web.val.length", MinimumLength, MaximumLength);
}

public sealed class LocCompareAttribute(string other) : CompareAttribute(other)
{
    public override string FormatErrorMessage(string name) => Loc.T("web.val.match");
}

/// <summary>Identity's own error texts (password rules, duplicate email…) in the user's language.</summary>
public sealed class LocalizedIdentityErrors : IdentityErrorDescriber
{
    private static IdentityError E(string code, string key, params object[] args) => new() { Code = code, Description = Loc.F(key, args) };

    public override IdentityError DefaultError() => E(nameof(DefaultError), "web.idn.default");
    public override IdentityError PasswordTooShort(int length) => E(nameof(PasswordTooShort), "web.idn.short", length);
    public override IdentityError PasswordRequiresNonAlphanumeric() => E(nameof(PasswordRequiresNonAlphanumeric), "web.idn.symbol");
    public override IdentityError PasswordRequiresDigit() => E(nameof(PasswordRequiresDigit), "web.idn.digit");
    public override IdentityError PasswordRequiresLower() => E(nameof(PasswordRequiresLower), "web.idn.lower");
    public override IdentityError PasswordRequiresUpper() => E(nameof(PasswordRequiresUpper), "web.idn.upper");
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => E(nameof(PasswordRequiresUniqueChars), "web.idn.unique", uniqueChars);
    public override IdentityError PasswordMismatch() => E(nameof(PasswordMismatch), "web.idn.mismatch");
    public override IdentityError DuplicateUserName(string userName) => E(nameof(DuplicateUserName), "web.idn.taken", userName);
    public override IdentityError DuplicateEmail(string email) => E(nameof(DuplicateEmail), "web.idn.taken", email);
    public override IdentityError InvalidEmail(string? email) => E(nameof(InvalidEmail), "web.idn.email");
    public override IdentityError InvalidUserName(string? userName) => E(nameof(InvalidUserName), "web.idn.email");
    public override IdentityError UserLockoutNotEnabled() => E(nameof(UserLockoutNotEnabled), "web.idn.default");
    public override IdentityError LoginAlreadyAssociated() => E(nameof(LoginAlreadyAssociated), "web.idn.linked");
}

/// <summary>The password rules the server enforces, as one line shown before anyone types.</summary>
public static class PasswordRules
{
    public static string Hint(Microsoft.AspNetCore.Identity.PasswordOptions options)
    {
        var needs = new List<string>();
        if (options.RequireUppercase) needs.Add(Loc.T("web.pw.upper"));
        if (options.RequireLowercase) needs.Add(Loc.T("web.pw.lower"));
        if (options.RequireDigit) needs.Add(Loc.T("web.pw.digit"));
        if (options.RequireNonAlphanumeric) needs.Add(Loc.T("web.pw.symbol"));
        if (needs.Count == 0) return Loc.F("web.pw.length", options.RequiredLength);
        var list = needs.Count == 1 ? needs[0] : Loc.F("web.pw.and", string.Join(", ", needs[..^1]), needs[^1]);
        return Loc.F("web.pw.rules", options.RequiredLength, list);
    }
}

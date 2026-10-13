using System.Globalization;

namespace Queueify.Services;

public sealed record CountryOption(string Code, string Name)
{
    /// <summary>What screen readers say for the item in the Settings list.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The countries offered in Settings: where Tidal is available, all of which are Spotify markets
/// too, so searches work on both services. Source: support.tidal.com, "Where Tidal is Available".
/// Names are kept here in English and Spanish because Windows can't name a region in a chosen language.
/// </summary>
public static class Countries
{
    private static readonly (string Code, string English, string Spanish)[] Table =
    [
        ("AL", "Albania", "Albania"),
        ("AD", "Andorra", "Andorra"),
        ("AR", "Argentina", "Argentina"),
        ("AU", "Australia", "Australia"),
        ("AT", "Austria", "Austria"),
        ("BE", "Belgium", "Bélgica"),
        ("BA", "Bosnia and Herzegovina", "Bosnia y Herzegovina"),
        ("BR", "Brazil", "Brasil"),
        ("BG", "Bulgaria", "Bulgaria"),
        ("CA", "Canada", "Canadá"),
        ("CL", "Chile", "Chile"),
        ("CO", "Colombia", "Colombia"),
        ("HR", "Croatia", "Croacia"),
        ("CY", "Cyprus", "Chipre"),
        ("CZ", "Czechia", "Chequia"),
        ("DK", "Denmark", "Dinamarca"),
        ("DO", "Dominican Republic", "República Dominicana"),
        ("EE", "Estonia", "Estonia"),
        ("FI", "Finland", "Finlandia"),
        ("FR", "France", "Francia"),
        ("DE", "Germany", "Alemania"),
        ("GR", "Greece", "Grecia"),
        ("HK", "Hong Kong", "Hong Kong"),
        ("HU", "Hungary", "Hungría"),
        ("IS", "Iceland", "Islandia"),
        ("IE", "Ireland", "Irlanda"),
        ("IL", "Israel", "Israel"),
        ("IT", "Italy", "Italia"),
        ("JM", "Jamaica", "Jamaica"),
        ("LV", "Latvia", "Letonia"),
        ("LI", "Liechtenstein", "Liechtenstein"),
        ("LT", "Lithuania", "Lituania"),
        ("LU", "Luxembourg", "Luxemburgo"),
        ("MY", "Malaysia", "Malasia"),
        ("MT", "Malta", "Malta"),
        ("MX", "Mexico", "México"),
        ("MC", "Monaco", "Mónaco"),
        ("ME", "Montenegro", "Montenegro"),
        ("NL", "Netherlands", "Países Bajos"),
        ("NZ", "New Zealand", "Nueva Zelanda"),
        ("NG", "Nigeria", "Nigeria"),
        ("MK", "North Macedonia", "Macedonia del Norte"),
        ("NO", "Norway", "Noruega"),
        ("PE", "Peru", "Perú"),
        ("PL", "Poland", "Polonia"),
        ("PT", "Portugal", "Portugal"),
        ("PR", "Puerto Rico", "Puerto Rico"),
        ("RO", "Romania", "Rumania"),
        ("RS", "Serbia", "Serbia"),
        ("SG", "Singapore", "Singapur"),
        ("SK", "Slovakia", "Eslovaquia"),
        ("SI", "Slovenia", "Eslovenia"),
        ("ZA", "South Africa", "Sudáfrica"),
        ("ES", "Spain", "España"),
        ("SE", "Sweden", "Suecia"),
        ("CH", "Switzerland", "Suiza"),
        ("TH", "Thailand", "Tailandia"),
        ("UG", "Uganda", "Uganda"),
        ("AE", "United Arab Emirates", "Emiratos Árabes Unidos"),
        ("GB", "United Kingdom", "Reino Unido"),
        ("US", "United States", "Estados Unidos"),
    ];

    public static IReadOnlyList<string> Codes { get; } = Table.Select(c => c.Code).ToArray();

    /// <summary>
    /// The list for the Settings dropdown, named and sorted for <paramref name="culture"/>. A saved
    /// country outside the list (from an older version or another PC) is kept, so it isn't lost silently.
    /// </summary>
    public static IReadOnlyList<CountryOption> Options(string savedCode, CultureInfo culture)
    {
        bool spanish = culture.TwoLetterISOLanguageName == "es";
        var options = Table.Select(c => new CountryOption(c.Code, spanish ? c.Spanish : c.English)).ToList();

        var code = savedCode.Trim().ToUpperInvariant();
        if (code.Length == 2 && code.All(char.IsAsciiLetterUpper) && !Codes.Contains(code))
            options.Add(new CountryOption(code, NameOf(code)));

        return options.OrderBy(o => o.Name, StringComparer.Create(culture, ignoreCase: true)).ToList();
    }

    private static string NameOf(string code)
    {
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }
}

namespace Queueify.Services;

/// <summary>
/// Which release years are believed. One rule for the PC app, the sync client, the web server and imports, so a
/// year one front end keeps is never thrown away by another (they would otherwise never converge).
/// </summary>
public static class AlbumYear
{
    public const int Min = 1000;
    public const int Max = 3000;

    public static bool IsValid(int year) => year is >= Min and <= Max;

    /// <summary>
    /// The year when it is believed, otherwise null. Takes 64-bit numbers so a value read from a file is
    /// bounded before it is narrowed (a huge number must not wrap into range).
    /// </summary>
    public static int? OrNull(long? year) => year is long y && y is >= Min and <= Max ? (int)y : null;
}

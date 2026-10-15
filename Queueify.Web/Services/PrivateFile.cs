namespace Queueify.Web.Services;

/// <summary>Files that hold one user's data in a shared folder such as the temp folder.</summary>
public static class PrivateFile
{
    /// <summary>
    /// Creates a new file only its owner can read (0600 on Linux and macOS; Windows keeps the profile's own ACL, and
    /// setting a Unix mode there throws). Fails if the name exists, so a pre-placed file or link is never reused.
    /// </summary>
    public static FileStream CreateNew(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}

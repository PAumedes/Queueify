using System.Security.Cryptography;

namespace Queueify.Web.Ocr;

public sealed record TessdataFile(string Language, string Url, string Sha256);

/// <summary>
/// Makes sure the Spanish and English models are on disk. Left alone, it keeps its own copy under the data
/// folder, downloaded once from tessdata_fast at a pinned release and accepted only when the SHA-256
/// matches; <c>Ocr:TessdataPath</c> hands the job to an administrator instead.
/// </summary>
public sealed class TessdataFetcher(HttpClient http, string managedFolder, string configuredFolder, IReadOnlyList<TessdataFile>? files = null)
{
    // tessdata_fast release 4.1.0: the small, quick models, enough for printed magazine text.
    public static IReadOnlyList<TessdataFile> Pinned { get; } =
    [
        new("spa", "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/spa.traineddata", "6F2E04D02774A18F01BED44B1111F2CD7F3BA7AC9DC4373CD3F898A40EA6B464"),
        new("eng", "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/4.1.0/eng.traineddata", "7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2"),
    ];

    private readonly SemaphoreSlim _once = new(1, 1);
    private bool _ready;

    /// <summary>The folder holding the models, downloading what's missing the first time.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        if (configuredFolder.Length > 0) return configuredFolder;
        if (_ready) return managedFolder;

        await _once.WaitAsync(ct);
        try
        {
            if (_ready) return managedFolder;
            Directory.CreateDirectory(managedFolder);
            foreach (var file in files ?? Pinned) await EnsureFileAsync(file, ct);
            _ready = true;
            return managedFolder;
        }
        finally
        {
            _once.Release();
        }
    }

    private async Task EnsureFileAsync(TessdataFile file, CancellationToken ct)
    {
        var path = Path.Combine(managedFolder, file.Language + ".traineddata");
        if (File.Exists(path) && Matches(await File.ReadAllBytesAsync(path, ct), file)) return;

        byte[] body;
        try
        {
            body = await http.GetByteArrayAsync(file.Url, ct);
        }
        catch (HttpRequestException)
        {
            throw new PhotoException(PhotoProblem.EngineMissing);
        }
        if (!Matches(body, file)) throw new PhotoException(PhotoProblem.EngineMissing);

        var partial = path + ".part";
        await File.WriteAllBytesAsync(partial, body, ct);
        File.Move(partial, path, overwrite: true);
    }

    private static bool Matches(byte[] body, TessdataFile file) =>
        Convert.ToHexString(SHA256.HashData(body)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Queueify.Models;
using Queueify.Services;
using Queueify.Web.Storage;

namespace Queueify.Web.Services;

public enum ImportProblem { Invalid, TooLarge, Busy }

public sealed class ImportException(ImportProblem problem) : Exception(problem.ToString())
{
    public ImportProblem Problem { get; } = problem;
}

/// <param name="Added">Albums that were not here before.</param>
/// <param name="AlreadyHere">Albums already here (an older or equal copy, or one the user deleted here).</param>
/// <param name="Skipped">Rows that could not be used (no title, no date, deleted in the file, odd values).</param>
public sealed record ImportReport(int Added, int AlreadyHere, int Skipped);

/// <summary>
/// Backup and restore of a user's queue: JSON and CSV out; the desktop's queueify.db or an exported JSON in.
/// An upload is untrusted. A database is copied to a private temp file, opened read-only with the schema
/// distrusted, checked to hold a real <c>items</c> table, and read row by row with every value cut and
/// range-checked; it is never attached to a live database. What survives goes in through <see cref="BacklogStore"/>.
/// </summary>
public sealed partial class DataTransfer(UserStores stores)
{
    public const int MaxBytes = 25 * 1024 * 1024;
    public const int MaxRows = 20_000;
    private const int MaxText = 500;
    private const string Format = "queueify-export";
    private static readonly byte[] SqliteMagic = "SQLite format 3\0"u8.ToArray();

    // ── export ──

    public string ExportJson(string userId)
    {
        var albums = stores.QueueOf(userId).LoadAll().OrderBy(i => i.AddedAt).Select(i => new SyncAlbum(
            i.Uid, i.Artist, i.Album, i.Year, i.Source, i.CoverUrl, i.SpotifyId, i.TidalId, i.AddedAt, i.ListenedAt, i.Rating, i.UpdatedAt, false));
        return JsonSerializer.Serialize(new { format = Format, version = 1, albums }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public string ExportCsv(string userId)
    {
        var csv = new StringBuilder("artist,album,year,source,added,listened,rating,spotify_id,tidal_id,uid\r\n");
        foreach (var i in stores.QueueOf(userId).LoadAll().OrderBy(i => i.AddedAt))
        {
            csv.AppendJoin(',',
                Cell(i.Artist), Cell(i.Album), i.Year?.ToString(CultureInfo.InvariantCulture) ?? "", Cell(i.Source ?? ""),
                i.AddedAt.ToString("O", CultureInfo.InvariantCulture), i.ListenedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                i.Rating >= 0 ? i.Rating.ToString(CultureInfo.InvariantCulture) : "", Cell(i.SpotifyId ?? ""), Cell(i.TidalId ?? ""), Cell(i.Uid));
            csv.Append("\r\n");
        }
        return csv.ToString();
    }

    /// <summary>
    /// One CSV cell. A text starting with = + - @ (or a tab or return) would run as a formula when the file is
    /// opened in a spreadsheet, so it gets a leading apostrophe, which spreadsheets show as plain text.
    /// </summary>
    private static string Cell(string text)
    {
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 || text.StartsWith(' ') || text.EndsWith(' ')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    // ── import ──

    /// <summary>Reads a queueify.db or an exported JSON, told apart by the first bytes, into the user's queue.</summary>
    public ImportReport Import(string userId, byte[] upload)
    {
        if (upload.Length > MaxBytes) throw new ImportException(ImportProblem.TooLarge);

        // One import per user at a time: a second one (a retry after a slow answer) is turned away, not interleaved.
        var gate = Gates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        if (!gate.Wait(0)) throw new ImportException(ImportProblem.Busy);
        try
        {
            var rows = upload.AsSpan().StartsWith(SqliteMagic) ? ReadDatabase(upload) : ReadJson(upload);

            var items = new List<BacklogItem>();
            int skipped = 0;
            foreach (var row in rows)
            {
                if (row.Deleted || Build(row) is not { } item) skipped++;
                else items.Add(item);
            }
            // Newest change wins, as in sync, by each album's own last edit; an album deleted here stays deleted.
            var (added, here) = stores.QueueOf(userId).MergeBackup(items);
            return new ImportReport(added, here, skipped);
        }
        finally
        {
            gate.Release();
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    private sealed record Row(string? Uid, string? Artist, string? Album, long? Year, string? Source, string? CoverUrl, string? SpotifyId,
        string? TidalId, string? AddedAt, string? ListenedAt, double? Rating, string? UpdatedAt, bool Deleted);

    private static IEnumerable<Row> ReadJson(byte[] upload)
    {
        try
        {
            var body = upload.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()) ? upload.AsMemory(3) : upload.AsMemory();   // Notepad and PowerShell add a BOM
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format
                || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1
                || !root.TryGetProperty("albums", out var albums) || albums.ValueKind != JsonValueKind.Array)
                throw new ImportException(ImportProblem.Invalid);
            if (albums.GetArrayLength() > MaxRows) throw new ImportException(ImportProblem.TooLarge);

            var rows = new List<Row>();
            foreach (var a in albums.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) { rows.Add(new Row(null, null, null, null, null, null, null, null, null, null, null, null, false)); continue; }
                rows.Add(new Row(
                    Str(a, "uid"), Str(a, "artist"), Str(a, "album"), Num(a, "year") is { } y ? (long)y : null, Str(a, "source"), Str(a, "coverUrl"),
                    Str(a, "spotifyId"), Str(a, "tidalId"), Str(a, "addedAt"), Str(a, "listenedAt"), Num(a, "rating"), Str(a, "updatedAt"),
                    a.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True));
            }
            return rows;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new ImportException(ImportProblem.Invalid);
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Cut(v.GetString()) : null;

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static List<Row> ReadDatabase(byte[] upload)
    {
        // Our own file name in the temp folder, so nothing the upload says picks a path.
        var path = Path.Combine(Path.GetTempPath(), $"queueify-import-{Guid.NewGuid():N}.db");
        try
        {
            using (var file = PrivateFile.CreateNew(path)) file.Write(upload);

            // immutable: no journal or side files are looked for or created; read-only: nothing can be written.
            var uri = "file:" + path.Replace('\\', '/').Replace("%", "%25").Replace("?", "%3f").Replace("#", "%23");
            var connection = new SqliteConnectionStringBuilder { DataSource = uri + "?immutable=1", Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using var db = new SqliteConnection(connection.ToString());
            db.Open();
            Run(db, "PRAGMA trusted_schema = OFF");
            Run(db, "PRAGMA query_only = ON");

            using (var kind = db.CreateCommand())
            {
                kind.CommandText = "SELECT type, sql FROM sqlite_master WHERE name = 'items' COLLATE NOCASE";
                using var r = kind.ExecuteReader();
                // A real table only: a view or a virtual table could run anything when read.
                if (!r.Read() || r.GetString(0) != "table" || (r.IsDBNull(1) ? "" : r.GetString(1)).Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase))
                    throw new ImportException(ImportProblem.Invalid);
            }

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = db.CreateCommand())
            {
                info.CommandText = "PRAGMA table_xinfo(items)";
                using var r = info.ExecuteReader();
                while (r.Read())
                {
                    // hidden != 0 marks generated columns: their expressions run on every read (a few bytes of file
                    // can ask for gigabytes of memory), so a table that has one is refused.
                    if (r.GetInt64(6) != 0) throw new ImportException(ImportProblem.Invalid);
                    columns.Add(r.GetString(1));
                }
            }
            if (!columns.IsSupersetOf(["artist", "album", "added_at"])) throw new ImportException(ImportProblem.Invalid);

            string Text(string c) => columns.Contains(c) ? $"substr(CAST({c} AS TEXT), 1, {MaxText})" : "NULL";
            string Number(string c) => columns.Contains(c) ? $"CASE WHEN typeof({c}) IN ('integer','real') THEN {c} END" : "NULL";

            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT {Text("uid")}, {Text("artist")}, {Text("album")}, {Number("year")}, {Text("source")}, {Text("cover_url")}, "
                + $"{Text("spotify_id")}, {Text("tidal_id")}, {Text("added_at")}, {Text("listened_at")}, {Number("rating")}, {Text("updated_at")}, {Number("deleted")} "
                + $"FROM main.items LIMIT {MaxRows + 1}";
            var rows = new List<Row>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (rows.Count == MaxRows) throw new ImportException(ImportProblem.TooLarge);
                string? S(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                rows.Add(new Row(S(0), S(1), S(2), reader.IsDBNull(3) ? null : (long)reader.GetDouble(3), S(4), S(5), S(6), S(7), S(8), S(9),
                    reader.IsDBNull(10) ? null : reader.GetDouble(10), S(11), !reader.IsDBNull(12) && reader.GetDouble(12) != 0));
            }
            return rows;
        }
        catch (SqliteException)
        {
            throw new ImportException(ImportProblem.Invalid);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Run(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // ── one row to one album ──

    private static BacklogItem? Build(Row row)
    {
        var artist = Tidy(row.Artist);
        var album = Tidy(row.Album);
        if (artist.Length == 0 && album.Length == 0) return null;
        if (ParseDate(row.AddedAt) is not { } added) return null;

        return new BacklogItem
        {
            Uid = SafeId(row.Uid, 64) ?? Fingerprint(artist, album, added),
            Artist = artist,
            Album = album,
            Year = row.Year is >= 1000 and <= 3000 ? (int)row.Year : null,
            Source = Tidy(row.Source) is { Length: > 0 } source ? source : null,
            CoverUrl = row.CoverUrl is { } cover && cover.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(cover, UriKind.Absolute, out _) ? cover : null,
            SpotifyId = SafeId(row.SpotifyId, 64),
            TidalId = SafeId(row.TidalId, 64),
            AddedAt = added,
            // The album's own last edit (never in the future, so it can't beat real edits); its added time when the file has none.
            UpdatedAt = new[] { ParseDate(row.UpdatedAt) ?? added, DateTime.UtcNow }.Min().ToUniversalTime(),
            ListenedAt = ParseDate(row.ListenedAt),
            Rating = row.Rating is -1 or (>= 0 and <= 5) ? row.Rating.Value : -1,
        };
    }

    private static string? Cut(string? s) => s is null ? null : s.Length > MaxText ? s[..MaxText] : s;

    /// <summary>No control characters, no stray spaces, at most 500 characters.</summary>
    private static string Tidy(string? s) => s is null ? "" : new string(Cut(s)!.Where(c => !char.IsControl(c)).ToArray()).Trim();

    private static string? SafeId(string? s, int max) => s is not null && s.Length <= max && IdShape().IsMatch(s) ? s : null;

    /// <summary>The same album in the same file always gets the same id, so importing it twice never duplicates.</summary>
    private static string Fingerprint(string artist, string album, DateTime added) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{artist}\n{album}\n{added.ToString("O", CultureInfo.InvariantCulture)}")))[..32].ToLowerInvariant();

    /// <summary>A date with no zone is taken as UTC, so the server's time zone never changes what an import means.</summary>
    private static DateTime? ParseDate(string? s) =>
        s is not null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) && d.Year is >= 1950 and <= 2999
            ? (d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d)
            : null;

    [GeneratedRegex("^[A-Za-z0-9_:.-]+$")]
    private static partial Regex IdShape();
}

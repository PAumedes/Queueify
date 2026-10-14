using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Queueify.Launcher;

/// <summary>Where the appended app is in the exe, and the name of its folder once unpacked.</summary>
public sealed record BundleInfo(long Offset, long Length, string Id);

/// <summary>The appended app doesn't match its id: a broken download or a damaged disk.</summary>
public sealed class BundleDamagedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The download is the starter exe followed by the app folder as one Brotli-compressed tar, then a
/// trailer: the archive's length, its id (a hash of it, which names the unpacked folder) and a marker.
/// Windows runs an exe from its headers, so whatever is appended after the starter doesn't disturb it.
/// </summary>
public static class Bundle
{
    private static readonly byte[] Marker = "QFYAPP01"u8.ToArray();
    private const int IdLength = 32;
    private const int TrailerLength = sizeof(long) + IdLength + 8;
    private const string Complete = ".complete";

    // Same bytes for the same files, so an unchanged version keeps its id and its unpacked folder.
    private static readonly DateTimeOffset FixedTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static void Write(string starterExe, string appFolder, string outputExe)
    {
        var archive = outputExe + ".archive";
        try
        {
            using (var file = File.Create(archive))
            using (var brotli = new BrotliCompressingStream(file))
            using (var tar = new TarWriter(brotli, TarEntryFormat.Gnu, leaveOpen: true))
            {
                var root = Path.GetFullPath(appFolder);
                var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
                    .OrderBy(f => f, StringComparer.Ordinal);
                foreach (var name in files)
                {
                    using var data = File.OpenRead(Path.Combine(root, name));
                    tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name)
                    {
                        DataStream = data,
                        ModificationTime = FixedTime, AccessTime = FixedTime, ChangeTime = FixedTime,
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    });
                }
            }

            byte[] id;
            using (var file = File.OpenRead(archive)) id = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(file))[..IdLength]);

            using var output = File.Create(outputExe);
            using (var starter = File.OpenRead(starterExe)) starter.CopyTo(output);
            long length;
            using (var file = File.OpenRead(archive)) { length = file.Length; file.CopyTo(output); }
            var trailer = new byte[TrailerLength];
            BinaryPrimitives.WriteInt64LittleEndian(trailer, length);
            id.CopyTo(trailer, sizeof(long));
            Marker.CopyTo(trailer, sizeof(long) + IdLength);
            output.Write(trailer);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    /// <summary>The appended app, or null when this exe has none (a plain build of the starter).</summary>
    public static BundleInfo? Read(Stream exe)
    {
        if (exe.Length < TrailerLength) return null;
        var trailer = new byte[TrailerLength];
        exe.Seek(-TrailerLength, SeekOrigin.End);
        exe.ReadExactly(trailer);
        if (!trailer.AsSpan(sizeof(long) + IdLength).SequenceEqual(Marker)) return null;
        var length = BinaryPrimitives.ReadInt64LittleEndian(trailer);
        var offset = exe.Length - TrailerLength - length;
        if (length <= 0 || offset < 0) return null;
        var id = Encoding.ASCII.GetString(trailer, sizeof(long), IdLength);
        return id.All(char.IsAsciiHexDigitLower) ? new BundleInfo(offset, length, id) : null;
    }

    /// <summary>
    /// The app's folder for this version, unpacked if it isn't yet. Unpacks into a folder of its own,
    /// checks the archive against its id, then renames it into place (see <see cref="PlaceUnpacked"/>).
    /// </summary>
    public static string Unpack(Stream exe, BundleInfo info, string cacheFolder)
    {
        var target = Path.Combine(cacheFolder, info.Id);
        if (IsComplete(target)) return target;

        var temp = Path.Combine(cacheFolder, $"{info.Id}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            Extract(exe, info, temp);
            File.WriteAllText(Path.Combine(temp, Complete), "");
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return PlaceUnpacked(temp, target);
    }

    private static void Extract(Stream exe, BundleInfo info, string folder)
    {
        // Checked before anything is written: Brotli has no checksum of its own, and the id is the
        // archive's hash. About 20 MB, read in a moment.
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            exe.Seek(info.Offset, SeekOrigin.Begin);
            new Slice(exe, info.Length, hash).ReadToEnd();
            if (Convert.ToHexStringLower(hash.GetHashAndReset())[..IdLength] != info.Id)
                throw new BundleDamagedException("The appended app doesn't match its id.");
        }

        exe.Seek(info.Offset, SeekOrigin.Begin);
        var archive = new Slice(exe, info.Length, null);
        try
        {
            using var brotli = new BrotliStream(archive, CompressionMode.Decompress, leaveOpen: true);
            using var tar = new TarReader(brotli);
            var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
            while (tar.GetNextEntry() is { } entry)
            {
                var path = Path.GetFullPath(Path.Combine(root, entry.Name));
                if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.EntryType != TarEntryType.RegularFile) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or EndOfStreamException or FormatException)
        {
            throw new BundleDamagedException("The appended app can't be read.", ex);
        }
    }

    /// <summary>
    /// Moves a freshly unpacked, complete folder into place and says which folder to run. A complete folder
    /// already there is never touched (another copy started at the same moment may be running from it);
    /// a broken leftover that can't be replaced is left, and the fresh copy runs from where it is.
    /// </summary>
    public static string PlaceUnpacked(string unpacked, string target)
    {
        if (IsComplete(target))
        {
            TryDelete(unpacked);
            return target;
        }
        try
        {
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(unpacked, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!IsComplete(target)) return unpacked;
            TryDelete(unpacked);
            return target;
        }
    }

    private static bool IsComplete(string folder) => File.Exists(Path.Combine(folder, Complete));

    /// <summary>
    /// Deletes the folders of other versions. One still running is left whole for next time: renaming a
    /// folder fails while a program runs from it, so each is renamed first and only then deleted. Another
    /// unpacking in progress (".tmp-", this version's or another's) is left alone unless it was abandoned long ago.
    /// </summary>
    public static void RemoveOtherVersions(string cacheFolder, string keepId)
    {
        if (!Directory.Exists(cacheFolder)) return;
        foreach (var dir in Directory.GetDirectories(cacheFolder))
        {
            var name = Path.GetFileName(dir);
            if (name == keepId) continue;
            // Any version's unpacking from the last hour may still be under way (or running, which the
            // rename below would refuse anyway).
            if (name.Contains(".tmp-") && Directory.GetLastWriteTimeUtc(dir) > DateTime.UtcNow.AddHours(-1)) continue;
            var doomed = name.Contains(".old-") ? dir : Path.Combine(cacheFolder, $"{name}.old-{Guid.NewGuid():N}");
            try
            {
                if (doomed != dir) Directory.Move(dir, doomed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            TryDelete(doomed);
        }
    }
    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Brotli at its strongest, with the largest window: the app folder is one big archive.</summary>
    private sealed class BrotliCompressingStream(Stream inner) : Stream
    {
        private BrotliEncoder _encoder = new(quality: 11, window: 24);
        private readonly byte[] _buffer = new byte[1 << 16];

        public override void Write(ReadOnlySpan<byte> source)
        {
            while (!source.IsEmpty)
            {
                _encoder.Compress(source, _buffer, out int read, out int written, isFinalBlock: false);
                inner.Write(_buffer, 0, written);
                source = source[read..];
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                System.Buffers.OperationStatus status;
                do
                {
                    status = _encoder.Compress([], _buffer, out _, out int written, isFinalBlock: true);
                    inner.Write(_buffer, 0, written);
                } while (status == System.Buffers.OperationStatus.DestinationTooSmall);
                _encoder.Dispose();
            }
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>The archive's bytes only, so decompression can't run into the trailer; hashed as they're read.</summary>
    private sealed class Slice(Stream inner, long length, IncrementalHash? hash) : Stream
    {
        private readonly long _length = length;
        private long _left = length;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_left == 0) return 0;
            int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            if (n == 0) throw new EndOfStreamException();
            hash?.AppendData(buffer[..n]);
            _left -= n;
            return n;
        }

        /// <summary>Reads (and hashes) the whole archive.</summary>
        public void ReadToEnd()
        {
            var buffer = new byte[1 << 16];
            while (Read(buffer) > 0) { }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _left; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

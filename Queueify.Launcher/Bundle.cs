using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Queueify.Launcher;

/// <summary>Where the appended app is in the exe, and the name of its folder once unpacked.</summary>
public sealed record BundleInfo(long Offset, long Length, string Id);

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
    /// The app's folder for this version, unpacked if it isn't yet. Unpacks beside it and renames into
    /// place, so a crash halfway or a second copy starting at the same time never leaves half an app.
    /// </summary>
    public static string Unpack(Stream exe, BundleInfo info, string cacheFolder)
    {
        var target = Path.Combine(cacheFolder, info.Id);
        if (File.Exists(Path.Combine(target, Complete))) return target;

        var temp = Path.Combine(cacheFolder, $"{info.Id}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            exe.Seek(info.Offset, SeekOrigin.Begin);
            using (var brotli = new BrotliStream(new Slice(exe, info.Length), CompressionMode.Decompress))
            using (var tar = new TarReader(brotli))
            {
                var root = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                while (tar.GetNextEntry() is { } entry)
                {
                    var path = Path.GetFullPath(Path.Combine(root, entry.Name));
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.EntryType != TarEntryType.RegularFile) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path, overwrite: false);
                }
            }
            File.WriteAllText(Path.Combine(temp, Complete), "");

            // What a crash mid-unpack by an older starter left behind.
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(temp, target);
        }
        catch (IOException) when (File.Exists(Path.Combine(target, Complete)))
        {
            // Another copy of Queueify starting at the same moment finished first.
        }
        finally
        {
            if (Directory.Exists(temp)) TryDelete(temp);
        }
        return target;
    }

    /// <summary>
    /// Deletes the folders of other versions. One still running is left whole for next time: renaming a
    /// folder fails while a program runs from it, so each is renamed first and only then deleted.
    /// </summary>
    public static void RemoveOtherVersions(string cacheFolder, string keepId)
    {
        if (!Directory.Exists(cacheFolder)) return;
        foreach (var dir in Directory.GetDirectories(cacheFolder))
        {
            var name = Path.GetFileName(dir);
            if (name == keepId || name.StartsWith(keepId + ".tmp-", StringComparison.Ordinal)) continue;
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

    /// <summary>The archive's bytes only, so decompression can't run into the trailer.</summary>
    private sealed class Slice(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _left = length;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_left == 0) return 0;
            int n = inner.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            _left -= n;
            return n;
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

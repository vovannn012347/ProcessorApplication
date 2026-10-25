using System.IO.Compression;

using ProcessingModule.Infrastructure.Util;

namespace ProcessingModule.Services.Installation;

public sealed class ArchiveLimits
{
    /// <summary>Total bytes written for all entries together.</summary>
    public long MaxTotalBytes { get; init; }

    /// <summary>Bytes written for any single entry.</summary>
    public long MaxEntryBytes { get; init; }

    /// <summary>Number of entries (files + folders) in the archive.</summary>
    public int MaxEntries { get; init; }

    /// <summary>Largest allowed (bytes written / compressed size) for one entry.</summary>
    public double MaxCompressionRatio { get; init; }

    /// <summary>The ratio check starts only after this many bytes, so tiny files can't trip it.</summary>
    public long RatioCheckMinBytes { get; init; } = 1024 * 1024;
}

public sealed class ArchiveExtractionException : Exception
{
    public ArchiveExtractionException(string message) : base(message) { }
    public ArchiveExtractionException(string message, Exception inner) : base(message, inner) { }
}

public sealed class ArchiveExtractionResult
{
    public int FileCount { get; init; }
    public int DirectoryCount { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// Extracts an untrusted .zip without trusting anything the archive says about itself.
///
/// Sizes from the zip headers are only used to reject obviously oversized archives early.
/// The real limits are enforced on the bytes actually written: each entry is copied through a
/// counting loop that stops as soon as the entry limit, the total limit or the compression-ratio
/// limit is exceeded, or the entry produces more data than its header declared.
/// </summary>
public static class SafeArchiveExtractor
{
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymlink = 0xA000;

    public static ArchiveExtractionResult Extract(string zipPath, string destinationDir, ArchiveLimits limits, CancellationToken ct = default)
    {
        string destRoot = ScriptPathRules.EnsureTrailingSeparator(Path.GetFullPath(destinationDir));
        Directory.CreateDirectory(destRoot);

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (InvalidDataException ex)
        {
            throw new ArchiveExtractionException($"The file is not a valid .zip archive ({ex.Message}).", ex);
        }

        using (archive)
        {
            var entries = archive.Entries;

            // --- Early rejection from header values (cheap, but not trusted) ---
            if (entries.Count == 0)
                throw new ArchiveExtractionException("The archive is empty.");

            if (entries.Count > limits.MaxEntries)
                throw new ArchiveExtractionException($"The archive has {entries.Count} entries; the limit is {limits.MaxEntries}.");

            long declaredTotal = 0;
            foreach (var e in entries)
            {
                if (e.Length > limits.MaxEntryBytes)
                    throw new ArchiveExtractionException($"'{e.FullName}' would be {FormatBytes(e.Length)}; the per-file limit is {FormatBytes(limits.MaxEntryBytes)}.");
                declaredTotal += e.Length;
            }
            if (declaredTotal > limits.MaxTotalBytes)
                throw new ArchiveExtractionException($"The archive would extract to {FormatBytes(declaredTotal)}; the limit is {FormatBytes(limits.MaxTotalBytes)}.");

            // --- Real extraction with counted bytes ---
            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var buffer = new byte[81920];
            long totalWritten = 0;
            int files = 0, dirs = 0;

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                string rawName = entry.FullName;
                bool isDirectory = rawName.EndsWith('/') || rawName.EndsWith('\\');

                if (!ScriptPathRules.TryNormalizeRelative(rawName, out var relative, out var pathError))
                {
                    // A bare "./" or "/" directory record has nothing to create; anything else is rejected.
                    if (isDirectory && entry.Length == 0 && rawName.Trim('/', '\\', '.').Length == 0) continue;
                    throw new ArchiveExtractionException($"Rejected entry '{rawName}': {pathError}.");
                }

                if (IsLink(entry))
                    throw new ArchiveExtractionException($"Rejected entry '{rawName}': symbolic links are not allowed.");

                if (!usedPaths.Add(relative))
                    throw new ArchiveExtractionException($"Rejected entry '{rawName}': another entry has the same name (names are compared ignoring case).");

                string target = Path.GetFullPath(Path.Combine(destRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(destRoot, ScriptPathRules.PathComparison))
                    throw new ArchiveExtractionException($"Rejected entry '{rawName}': it resolves outside the extraction folder.");

                try
                {
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(target);
                        dirs++;
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                    long written = 0;
                    uint crc = Crc32.Seed;
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            ct.ThrowIfCancellationRequested();

                            written += read;
                            totalWritten += read;

                            if (written > entry.Length)
                                throw new ArchiveExtractionException($"'{rawName}' produced more data than its header declares ({FormatBytes(entry.Length)}); the archive is corrupt or crafted.");

                            if (written > limits.MaxEntryBytes)
                                throw new ArchiveExtractionException($"'{rawName}' exceeded the per-file limit of {FormatBytes(limits.MaxEntryBytes)}.");

                            if (totalWritten > limits.MaxTotalBytes)
                                throw new ArchiveExtractionException($"Extraction exceeded the total limit of {FormatBytes(limits.MaxTotalBytes)}.");

                            if (written > limits.RatioCheckMinBytes &&
                                written > Math.Max(entry.CompressedLength, 1) * limits.MaxCompressionRatio)
                                throw new ArchiveExtractionException($"'{rawName}' expands more than {limits.MaxCompressionRatio:0}x its compressed size; rejected as a possible zip bomb.");

                            crc = Crc32.Update(crc, buffer, read);
                            output.Write(buffer, 0, read);
                        }
                    }

                    if (written != entry.Length)
                        throw new ArchiveExtractionException($"'{rawName}' is truncated: {FormatBytes(written)} extracted, {FormatBytes(entry.Length)} declared.");

                    // The runtime stops reading at the size the header declares, so a header that
                    // under-reports the size yields a silently truncated file. The CRC catches that
                    // (and any other corruption).
                    if (Crc32.Finish(crc) != entry.Crc32)
                        throw new ArchiveExtractionException($"'{rawName}' failed its CRC check; the archive is corrupt or its headers were altered.");

                    files++;
                }
                catch (ArchiveExtractionException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (InvalidDataException ex)
                {
                    throw new ArchiveExtractionException($"'{rawName}' could not be decompressed ({ex.Message}). Encrypted or unsupported compression methods are not supported.", ex);
                }
                catch (NotSupportedException ex)
                {
                    throw new ArchiveExtractionException($"'{rawName}' uses an unsupported zip feature ({ex.Message}).", ex);
                }
                catch (IOException ex)
                {
                    throw new ArchiveExtractionException($"'{rawName}' could not be written ({ex.Message}). A file and a folder may share the same name.", ex);
                }
            }

            if (files == 0)
                throw new ArchiveExtractionException("The archive contains no files.");

            return new ArchiveExtractionResult { FileCount = files, DirectoryCount = dirs, TotalBytes = totalWritten };
        }
    }

    private static bool IsLink(ZipArchiveEntry entry)
    {
        int attributes = entry.ExternalAttributes;
        int unixMode = (attributes >> 16) & 0xFFFF;
        if ((unixMode & UnixFileTypeMask) == UnixSymlink) return true;
        if ((attributes & (int)FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    /// <summary>CRC-32 (IEEE 802.3), as used by zip. Kept local to avoid a package dependency.</summary>
    private static class Crc32
    {
        public const uint Seed = 0xFFFFFFFFu;
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }

        public static uint Update(uint crc, byte[] data, int count)
        {
            for (int i = 0; i < count; i++) crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        public static uint Finish(uint crc) => crc ^ 0xFFFFFFFFu;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>Where one file's bytes sit inside a zip, and how they are compressed.</summary>
/// <param name="DataOffset">The first byte of the file's compressed data, from the start of the archive.</param>
/// <param name="CompressedSize">How many bytes to fetch from there.</param>
/// <param name="Size">What they inflate to.</param>
/// <param name="Deflated">Deflate when true; stored as is when false.</param>
internal readonly record struct ZipEntrySpan(long DataOffset, long CompressedSize, long Size, bool Deflated);

/// <summary>
/// Finds one file in a zip that is read a range at a time — a NuGet package on a CDN — so that
/// only that file's bytes are fetched, not the archive.
/// </summary>
/// <remarks>
/// <para>Three small reads and nothing assumed about the layout: the archive's last bytes for the
/// end-of-central-directory record, the central directory it points at, then the file's local
/// header, whose name and extra fields decide where its data starts. The sizes and the compression
/// come from the central directory, which is the one place they are always right: a file written
/// with a data descriptor (flag bit 3) has zeros for them in its local header.</para>
///
/// <para>ZIP64 archives and entries, archives spanning several disks, encrypted entries and any
/// compression but stored and deflate are refused with <see cref="InvalidDataException"/> rather
/// than read wrongly. So is anything that points past where the archive ends — a truncated file, or
/// a server that sent less than it was asked for.</para>
/// </remarks>
internal static class ZipEntryLocator
{
    private const uint EndOfCentralDirectorySignature = 0x06054b50;
    private const uint Zip64LocatorSignature = 0x07064b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint LocalHeaderSignature = 0x04034b50;
    private const int EndOfCentralDirectoryLength = 22;
    private const int Zip64LocatorLength = 20;
    private const int CentralHeaderLength = 46;
    internal const int LocalHeaderLength = 30;

    /// <summary>
    /// Enough of the archive's end to hold the end-of-central-directory record with the longest
    /// comment a zip can have, and the ZIP64 locator in front of it.
    /// </summary>
    internal const int TailLength = EndOfCentralDirectoryLength + ushort.MaxValue + Zip64LocatorLength;

    // A central directory bigger than this is not a package this app reads.
    private const int MaxCentralDirectory = 16 << 20;

    /// <summary>The last bytes of the archive, up to <c>count</c>, and the archive's whole length.</summary>
    internal delegate Task<(byte[] Tail, long Length)> TailReader(int count, CancellationToken token);

    /// <summary><c>count</c> bytes from <c>offset</c>; fewer only if the archive ends first.</summary>
    internal delegate Task<byte[]> RangeReader(long offset, int count, CancellationToken token);

    internal static async Task<ZipEntrySpan> LocateAsync(
        TailReader readTail, RangeReader readRange, string entryName, CancellationToken token)
    {
        var (tail, length) = await readTail(TailLength, token).ConfigureAwait(false);
        if (tail.Length > length)
            throw new InvalidDataException("zip: the archive's end is longer than the archive");
        var tailStart = length - tail.Length;
        var (directoryOffset, directorySize, endRecord) = ReadEndOfCentralDirectory(tail, tailStart);

        byte[] directory;
        if (directoryOffset >= tailStart)
            directory = tail.AsSpan((int)(directoryOffset - tailStart), (int)directorySize).ToArray();
        else
        {
            directory = await readRange(directoryOffset, (int)directorySize, token).ConfigureAwait(false);
            if (directory.Length != directorySize)
                throw new InvalidDataException("zip: the central directory is cut short");
        }

        var (localOffset, compressedSize, size, deflated) = FindInCentralDirectory(directory, entryName);
        if (localOffset + LocalHeaderLength > directoryOffset)
            throw new InvalidDataException($"zip: {entryName} starts past where the files end");

        var header = await readRange(localOffset, LocalHeaderLength, token).ConfigureAwait(false);
        var dataOffset = DataOffset(header, localOffset);
        if (dataOffset + compressedSize > directoryOffset || directoryOffset > endRecord)
            throw new InvalidDataException($"zip: {entryName} runs past where the files end");

        return new ZipEntrySpan(dataOffset, compressedSize, size, deflated);
    }

    /// <summary>The central directory's offset and size, and where the record saying so starts.</summary>
    internal static (long Offset, long Size, long EndRecord) ReadEndOfCentralDirectory(ReadOnlySpan<byte> tail, long tailStart)
    {
        // Backwards from the last place a record could start; a comment could hold the signature
        // too, so the comment length has to land exactly on the end.
        for (int at = tail.Length - EndOfCentralDirectoryLength; at >= 0 && tail.Length - at <= TailLength; at--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail[at..]) != EndOfCentralDirectorySignature) continue;
            var record = tail[at..];
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[20..]) != tail.Length - at - EndOfCentralDirectoryLength)
                continue;

            var disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var entriesHere = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);

            bool zip64Locator = at >= Zip64LocatorLength &&
                                BinaryPrimitives.ReadUInt32LittleEndian(tail[(at - Zip64LocatorLength)..]) == Zip64LocatorSignature;
            if (zip64Locator || entries == ushort.MaxValue || entriesHere == ushort.MaxValue ||
                size == uint.MaxValue || offset == uint.MaxValue)
                throw new InvalidDataException("zip: ZIP64 archives are not supported");
            if (disk != 0 || directoryDisk != 0 || entries != entriesHere)
                throw new InvalidDataException("zip: archives spanning several disks are not supported");

            long endRecord = tailStart + at;
            if (offset + (long)size > endRecord)
                throw new InvalidDataException("zip: the central directory runs past its end record (a truncated archive?)");
            if (size > MaxCentralDirectory)
                throw new InvalidDataException($"zip: a {size}-byte central directory is not one this reads");
            return (offset, size, endRecord);
        }

        throw new InvalidDataException("zip: no end-of-central-directory record (not a zip, or a truncated one)");
    }

    /// <summary>The entry named <paramref name="entryName"/>, from the central directory's own record of it.</summary>
    internal static (long LocalOffset, long CompressedSize, long Size, bool Deflated) FindInCentralDirectory(
        ReadOnlySpan<byte> directory, string entryName)
    {
        int at = 0;
        while (at < directory.Length)
        {
            if (directory.Length - at < CentralHeaderLength ||
                BinaryPrimitives.ReadUInt32LittleEndian(directory[at..]) != CentralHeaderSignature)
                throw new InvalidDataException("zip: the central directory is malformed");

            var header = directory[at..];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
            var method = BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[42..]);
            var next = at + CentralHeaderLength + nameLength + extraLength + commentLength;
            if (next > directory.Length)
                throw new InvalidDataException("zip: the central directory is malformed");

            // Bit 11: the name is UTF-8. Otherwise it is code page 437, which agrees with UTF-8 on
            // the ASCII names packages use.
            var nameBytes = header.Slice(CentralHeaderLength, nameLength);
            var name = (flags & 0x800) != 0 ? Encoding.UTF8.GetString(nameBytes) : Encoding.Latin1.GetString(nameBytes);
            if (name == entryName)
            {
                if (compressedSize == uint.MaxValue || size == uint.MaxValue || localOffset == uint.MaxValue)
                    throw new InvalidDataException($"zip: {entryName} is a ZIP64 entry, which is not supported");
                if ((flags & 0x1) != 0)
                    throw new InvalidDataException($"zip: {entryName} is encrypted");
                if (method is not (0 or 8))
                    throw new InvalidDataException($"zip: {entryName} uses compression method {method}, not stored or deflate");
                if (method == 0 && compressedSize != size)
                    throw new InvalidDataException($"zip: {entryName} is stored but its two sizes differ");
                return (localOffset, compressedSize, size, method == 8);
            }

            at = next;
        }

        throw new InvalidDataException($"zip: no {entryName} in the archive");
    }

    /// <summary>Where the data starts, from the entry's local header read at <paramref name="localOffset"/>.</summary>
    internal static long DataOffset(ReadOnlySpan<byte> localHeader, long localOffset)
    {
        if (localHeader.Length < LocalHeaderLength ||
            BinaryPrimitives.ReadUInt32LittleEndian(localHeader) != LocalHeaderSignature)
            throw new InvalidDataException("zip: no local header where the central directory says");
        // The local header's own name and extra field; its extra field need not match the
        // central directory's, which is why it is read at all.
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader[26..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(localHeader[28..]);
        return localOffset + LocalHeaderLength + nameLength + extraLength;
    }

    /// <summary>
    /// Inflates (or copies) an entry's compressed bytes from <paramref name="compressed"/> into
    /// <paramref name="output"/>. Throws <see cref="InvalidDataException"/> when they do not come
    /// to exactly <see cref="ZipEntrySpan.Size"/> bytes, or do not inflate at all.
    /// </summary>
    internal static async Task ExtractAsync(string compressed, string output, ZipEntrySpan span, CancellationToken token)
    {
        await using var input = new FileStream(compressed, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        if (input.Length != span.CompressedSize)
            throw new InvalidDataException($"zip: {input.Length} compressed bytes where {span.CompressedSize} were expected");
        await using var source = span.Deflated ? new DeflateStream(input, CompressionMode.Decompress) : (Stream)input;
        await using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long written = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            written += read;
            // Stops a stream that inflates to more than it says, rather than filling the disk.
            if (written > span.Size)
                throw new InvalidDataException($"zip: the entry inflates to more than {span.Size} bytes");
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }

        if (written != span.Size)
            throw new InvalidDataException($"zip: the entry inflates to {written} bytes, not {span.Size}");
    }
}

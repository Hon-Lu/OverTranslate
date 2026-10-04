using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

public sealed class ZipEntryLocatorTests : IDisposable
{
    private const string Entry = "bin/x64-win/DirectML.dll";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ot-zip-" + Guid.NewGuid().ToString("N"));

    public ZipEntryLocatorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    // Compresses well, so deflate and stored differ in size.
    private static readonly byte[] Library = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("MZ DirectML stand-in. ", 4000)));

    /// <summary>Random, so it does not compress: pushes the central directory out of the tail read.</summary>
    private static byte[] Noise(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    internal static byte[] Zip(
        CompressionLevel level = CompressionLevel.Optimal, bool seekable = true, string? comment = null,
        byte[]? library = null, int noise = 0)
    {
        var buffer = new MemoryStream();
        Stream target = seekable ? buffer : new ForwardOnly(buffer);
        using (var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (noise > 0)
                using (var stream = zip.CreateEntry("bin/x86-win/DirectML.dll", CompressionLevel.NoCompression).Open())
                    stream.Write(Noise(noise, 1));
            using (var stream = zip.CreateEntry(Entry, level).Open())
                stream.Write(library ?? Library);
            if (noise > 0)
                using (var stream = zip.CreateEntry("bin/arm64-win/DirectML.dll", CompressionLevel.NoCompression).Open())
                    stream.Write(Noise(noise, 2));
            if (comment is not null) zip.Comment = comment;
        }

        return buffer.ToArray();
    }

    internal static ZipEntryLocator.TailReader Tail(byte[] archive) => (count, _) =>
        Task.FromResult((archive[Math.Max(0, archive.Length - count)..], (long)archive.Length));

    internal static ZipEntryLocator.RangeReader Range(byte[] archive, List<(long, int)>? log = null) => (offset, count, _) =>
    {
        log?.Add((offset, count));
        var end = (int)Math.Min(archive.Length, offset + count);
        return Task.FromResult(offset >= archive.Length ? [] : archive[(int)offset..end]);
    };

    private static Task<ZipEntrySpan> Locate(byte[] archive, string entry = Entry) =>
        ZipEntryLocator.LocateAsync(Tail(archive), Range(archive), entry, default);

    private async Task<byte[]> Extract(byte[] archive, ZipEntrySpan span)
    {
        var compressed = Path.Combine(_folder, "compressed");
        var output = Path.Combine(_folder, "output");
        await File.WriteAllBytesAsync(compressed, archive[(int)span.DataOffset..(int)(span.DataOffset + span.CompressedSize)]);
        await ZipEntryLocator.ExtractAsync(compressed, output, span, default);
        return await File.ReadAllBytesAsync(output);
    }

    [Fact]
    public async Task ADeflatedEntry_IsFoundAndInflates()
    {
        var archive = Zip(CompressionLevel.Optimal);

        var span = await Locate(archive);

        Assert.True(span.Deflated);
        Assert.Equal(Library.Length, span.Size);
        Assert.True(span.CompressedSize < Library.Length / 10);
        Assert.Equal(Library, await Extract(archive, span));
    }

    [Fact]
    public async Task AStoredEntry_IsFoundAndCopied()
    {
        var archive = Zip(CompressionLevel.NoCompression);

        var span = await Locate(archive);

        Assert.False(span.Deflated);
        Assert.Equal(span.Size, span.CompressedSize);
        Assert.Equal(Library, await Extract(archive, span));
    }

    [Fact]
    public async Task AnEntryWrittenWithADataDescriptor_TakesItsSizesFromTheCentralDirectory()
    {
        // Written to a stream that cannot seek back, the local header carries zeros for the sizes.
        var archive = Zip(seekable: false);
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(LocalHeaderOf(archive) + 6)) & 0x8);

        var span = await Locate(archive);

        Assert.Equal(Library, await Extract(archive, span));
    }

    [Fact]
    public async Task AnArchiveComment_DoesNotHideTheEnd()
    {
        // The comment holds the record's signature itself; only the real record's length lands on the end.
        var archive = Zip(comment: "PK\u0005\u0006 not the end");

        Assert.Equal(Library, await Extract(archive, await Locate(archive)));
    }

    [Fact]
    public async Task ACentralDirectoryOutsideTheTail_IsReadWithARange_AndOnlyThreeReadsAreMade()
    {
        var archive = Zip(noise: 200_000);
        var reads = new List<(long, int)>();

        var span = await ZipEntryLocator.LocateAsync(Tail(archive), Range(archive, reads), Entry, default);

        Assert.Equal(Library, await Extract(archive, span));
        // The local header only: the central directory was within the tail read.
        Assert.Single(reads);
        Assert.Equal(ZipEntryLocator.LocalHeaderLength, reads[0].Item2);

        // Now with a short tail, so the central directory needs a read of its own.
        reads.Clear();
        ZipEntryLocator.TailReader shortTail = (_, _) => Task.FromResult((archive[^100..], (long)archive.Length));
        span = await ZipEntryLocator.LocateAsync(shortTail, Range(archive, reads), Entry, default);
        Assert.Equal(2, reads.Count);
        Assert.Equal(Library, await Extract(archive, span));
    }

    [Fact]
    public async Task ANameNotInTheArchive_IsAnError()
    {
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Locate(Zip(), "bin/x64-win/Missing.dll"));
        Assert.Contains("no bin/x64-win/Missing.dll", ex.Message);
        // Not a prefix or a different case either.
        await Assert.ThrowsAsync<InvalidDataException>(() => Locate(Zip(), "bin/x64-win/directml.dll"));
    }

    [Fact]
    public async Task AnArchiveCutShortAtTheEnd_HasNoEndRecord()
    {
        var archive = Zip();

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Locate(archive[..^10]));
        Assert.Contains("end-of-central-directory", ex.Message);
    }

    [Fact]
    public async Task AnArchiveMissingItsStart_PointsPastWhatIsThere()
    {
        // The end is intact, but every offset in it is now too far.
        var archive = Zip(noise: 200_000)[100_000..];

        await Assert.ThrowsAsync<InvalidDataException>(() => Locate(archive));
    }

    [Fact]
    public async Task ACentralDirectoryReadThatComesBackShort_IsAnError()
    {
        var archive = Zip(noise: 200_000);
        ZipEntryLocator.TailReader shortTail = (_, _) => Task.FromResult((archive[^100..], (long)archive.Length));
        ZipEntryLocator.RangeReader shortRange = (offset, count, _) =>
            Task.FromResult(archive[(int)offset..(int)(offset + count / 2)]);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipEntryLocator.LocateAsync(shortTail, shortRange, Entry, default));
        Assert.Contains("cut short", ex.Message);
    }

    [Fact]
    public async Task CompressedBytesCutShort_DoNotInflate()
    {
        var archive = Zip();
        var span = await Locate(archive);
        var compressed = Path.Combine(_folder, "compressed");
        await File.WriteAllBytesAsync(compressed, archive[(int)span.DataOffset..(int)(span.DataOffset + span.CompressedSize - 10)]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipEntryLocator.ExtractAsync(compressed, Path.Combine(_folder, "out"), span, default));
        // Claimed whole but inflating to less than the directory says.
        await File.WriteAllBytesAsync(compressed, archive[(int)span.DataOffset..(int)(span.DataOffset + span.CompressedSize)]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipEntryLocator.ExtractAsync(compressed, Path.Combine(_folder, "out"), span with { Size = span.Size + 1 }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ZipEntryLocator.ExtractAsync(compressed, Path.Combine(_folder, "out"), span with { Size = span.Size - 1 }, default));
    }

    [Fact]
    public async Task AZip64Archive_IsRefusedAsSuch()
    {
        var archive = Zip();
        var end = EndRecordOf(archive);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(end + 16), uint.MaxValue);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Locate(archive));
        Assert.Contains("ZIP64", ex.Message);
    }

    [Fact]
    public async Task AZip64Entry_IsRefusedAsSuch()
    {
        var archive = Zip();
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(EndRecordOf(archive) + 16));
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(central + 20), uint.MaxValue);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Locate(archive));
        Assert.Contains("ZIP64", ex.Message);
    }

    [Fact]
    public async Task AnUnknownCompressionMethod_IsRefused()
    {
        var archive = Zip();
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(EndRecordOf(archive) + 16));
        BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(central + 10), 14);   // LZMA

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Locate(archive));
        Assert.Contains("method 14", ex.Message);
    }

    private static int EndRecordOf(byte[] archive)
    {
        for (int at = archive.Length - 22; at >= 0; at--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(at)) == 0x06054b50)
                return at;
        throw new InvalidOperationException();
    }

    private static int LocalHeaderOf(byte[] archive)
    {
        var central = (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(EndRecordOf(archive) + 16));
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(central + 42));
    }

    /// <summary>A stream that cannot seek, so ZipArchive writes data descriptors.</summary>
    private sealed class ForwardOnly(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}

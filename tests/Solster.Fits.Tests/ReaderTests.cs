using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Xunit;

namespace Solster.Fits.Tests;

public class ReaderTests
{
    internal static Byte[] Image(Int32 bitpix, Byte[] payload, String[]? extra = null,
        Int32 width = 2, Int32 height = 1, Boolean extension = false)
    {
        var cards = new List<String> { extension ? "XTENSION= 'IMAGE   '" : "SIMPLE  = T",
            $"BITPIX  = {bitpix}", "NAXIS   = 2", $"NAXIS1  = {width}", $"NAXIS2  = {height}" };
        if (extension) cards.AddRange(["PCOUNT  = 0", "GCOUNT  = 1"]);
        cards.AddRange(extra ?? []); cards.Add("END");
        var bytes = new Byte[2880 + payload.Length];
        Array.Fill(bytes, (Byte)' ', 0, 2880);
        for (var i = 0; i < cards.Count; i++) Encoding.ASCII.GetBytes(cards[i], bytes.AsSpan(i * 80));
        payload.CopyTo(bytes, 2880);
        return bytes;
    }

    private static async Task Check<T>(Int32 bitpix, Byte[] bytes, T[] expected,
        String[]? extra = null, FitsPixelValueMode mode = FitsPixelValueMode.Physical)
        where T : unmanaged, INumberBase<T>
    {
        var reader = new FitsReader();
        using var stream = new FragmentedStream(Image(bitpix, bytes, extra));
        var header = await reader.ReadHeaderAsync(stream);
        var destination = new T[2];
        await reader.ReadPixelsAsync<T>(stream, header, destination, mode);
        Assert.Equal(expected, destination);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task EveryStandardPixelTypeDecodesInBigEndianOrder()
    {
        await Check<Byte>(8, [0, 255], [0, 255]);
        await Check<Int16>(16, [128, 0, 127, 255], [Int16.MinValue, Int16.MaxValue]);
        await Check<Int32>(32, [128, 0, 0, 0, 127, 255, 255, 255], [Int32.MinValue, Int32.MaxValue]);
        await Check<Int64>(64, [128, 0, 0, 0, 0, 0, 0, 0, 127, 255, 255, 255, 255, 255, 255, 255], [Int64.MinValue, Int64.MaxValue]);
        await Check<Single>(-32, [63, 192, 0, 0, 192, 16, 0, 0], [1.5f, -2.25f]);
        await Check<Double>(-64, [63, 248, 0, 0, 0, 0, 0, 0, 192, 2, 0, 0, 0, 0, 0, 0], [1.5, -2.25]);
    }

    [Fact]
    public async Task UnsignedOffsetsRemainExactIncludingAdjacentUInt64Values()
    {
        await Check<UInt16>(16, [128, 0, 127, 255], [0, UInt16.MaxValue], ["BZERO   = 32768"]);
        await Check<UInt32>(32, [128, 0, 0, 0, 127, 255, 255, 255], [0, UInt32.MaxValue], ["BZERO   = 2147483648"]);
        await Check<UInt64>(64, [127, 255, 255, 255, 255, 255, 255, 254, 127, 255, 255, 255, 255, 255, 255, 255],
            [UInt64.MaxValue - 1, UInt64.MaxValue], ["BZERO   = 9223372036854775808"]);
    }

    [Fact]
    public async Task ScalingBlankAndStoredValuesHaveExplicitSemantics()
    {
        await Check<Double>(16, [0, 3, 255, 255], [16, Double.NaN],
            ["BSCALE  = 2D0", "BZERO   = 10", "BLANK   = -1"]);
        await Check<Int16>(16, [0, 3, 255, 255], [3, -1],
            ["BSCALE  = 2", "BZERO   = 10", "BLANK   = -1"], FitsPixelValueMode.Stored);
        await Check<Single>(-32, [127, 192, 0, 0, 127, 128, 0, 0], [Single.NaN, Single.PositiveInfinity]);
        using var stream = new MemoryStream(Image(16, [255, 255, 0, 1], ["BLANK   = -1"]));
        var reader = new FitsReader(); var header = await reader.ReadHeaderAsync(stream);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadPixelsAsync<Int16>(stream, header, new Int16[2]));
    }

    [Fact]
    public async Task HeaderPreservesCardsStringsAndLifetimeAcrossReads()
    {
        var reader = new FitsReader();
        using var stream = new MemoryStream(Image(8, [1, 2], ["FILTER  = 'O''Brien / Ha' / comment", "COMMENT first", "COMMENT second"]));
        var header = await reader.ReadHeaderAsync(stream);
        Assert.Equal(2880, stream.Position);
        Assert.True(header.TryGetString("filter", out var filter)); Assert.Equal("O'Brien / Ha", filter);
        using var next = new MemoryStream(Image(8, [3, 4]));
        await reader.ReadHeaderAsync(next);
        Assert.Equal("COMMENT first", Encoding.ASCII.GetString(header.GetCard(6).Span).TrimEnd());
        Assert.Equal("COMMENT second", Encoding.ASCII.GetString(header.GetCard(7).Span).TrimEnd());
    }

    [Theory]
    [InlineData(FitsRegionReadMode.Sequential)]
    [InlineData(FitsRegionReadMode.Seek)]
    public async Task RectangleMatchesRowsAndLeavesOutputOwned(FitsRegionReadMode mode)
    {
        var reader = new FitsReader(2880);
        var payload = Enumerable.Range(0, 100 * 100).Select(i => (Byte)(i % 256)).ToArray();
        using var stream = new MemoryStream(Image(8, payload, width: 100, height: 100));
        var header = await reader.ReadHeaderAsync(stream);
        var output = new Byte[21 * 35];
        await reader.ReadRegionAsync<Byte>(stream, header, new FitsRegion(79, 65, 21, 35), output, mode);
        Assert.Equal(2880 + payload.Length, stream.Position);
        Assert.Equal(Enumerable.Range(65, 35).SelectMany(row => payload.Skip(row * 100 + 79).Take(21)), output);
        using var next = new MemoryStream(Image(8, [255, 255]));
        await reader.ReadHeaderAsync(next);
        Assert.Equal(payload[6579], output[0]);
    }

    [Fact]
    public async Task SequentialRegionRejectsTruncationOutsidePatch()
    {
        using var stream = new FragmentedStream(Image(8, new Byte[99], width: 10, height: 10));
        var reader = new FitsReader(); var header = await reader.ReadHeaderAsync(stream);
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await reader.ReadRegionAsync<Byte>(stream, header, new FitsRegion(0, 0, 1, 1), new Byte[1]));
    }

    [Fact]
    public async Task StreamingAwaitsConsumerAndRejectsConcurrentReuse()
    {
        var reader = new FitsReader();
        using var stream = new MemoryStream(Image(8, [1, 2, 3, 4], width: 4));
        var header = await reader.ReadHeaderAsync(stream);
        var scratch = new Byte[2]; var seen = new List<Byte>();
        await reader.StreamPixelsAsync<Byte>(stream, header, scratch, async (pixels, offset, token) =>
        {
            await Task.Yield();
            Assert.Equal(seen.Count, offset);
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await reader.ReadHeaderAsync(stream));
            seen.AddRange(pixels.ToArray());
        });
        Assert.Equal(new Byte[] { 1, 2, 3, 4 }, seen);
        Assert.Equal(new Byte[] { 3, 4 }, scratch);
    }

    [Fact]
    public async Task HduDiscoverySkipsPaddingAndFindsImageExtension()
    {
        var first = Image(8, [1, 2]); Array.Resize(ref first, 5760);
        using var stream = new FragmentedStream(first.Concat(Image(16, [0, 3, 0, 4], extension: true)).ToArray());
        var reader = new FitsReader();
        var hdu = await reader.ReadHduAsync(stream);
        Assert.True(hdu is FitsImageHeader);
        await reader.SkipHduAsync(stream, (FitsImageHeader)hdu.Value!);
        var extension = await reader.ReadHeaderAsync(stream);
        var output = new Int16[2]; await reader.ReadPixelsAsync<Int16>(stream, extension, output);
        Assert.Equal(new Int16[] { 3, 4 }, output);
        Assert.True(await reader.ReadHduAsync(stream) is null);
    }

    [Fact]
    public async Task UnsupportedTableCanBeIdentifiedAndSkipped()
    {
        var bytes = Image(8, [1, 2], ["PCOUNT  = 0", "GCOUNT  = 1"]);
        Encoding.ASCII.GetBytes("XTENSION= 'BINTABLE'".PadRight(80), bytes);
        Array.Resize(ref bytes, 5760);
        using var stream = new MemoryStream(bytes);
        var reader = new FitsReader(); var hdu = await reader.ReadHduAsync(stream);
        Assert.True(hdu is FitsUnsupportedHdu);
        var other = (FitsUnsupportedHdu)hdu.Value!;
        Assert.Equal("BINTABLE", other.ExtensionType);
        await reader.SkipHduAsync(stream, other.Header);
        Assert.True(await reader.ReadHduAsync(stream) is null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(80)]
    [InlineData(2879)]
    [InlineData(2881)]
    public async Task TruncatedHeaderAndPayloadFail(Int32 length)
    {
        using var stream = new FragmentedStream(Image(16, [0, 1, 0, 2])[..length]);
        var reader = new FitsReader();
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
        {
            var header = await reader.ReadHeaderAsync(stream);
            await reader.ReadPixelsAsync<Int16>(stream, header, new Int16[2]);
        });
    }

    [Fact]
    public async Task InvalidDimensionsAndMissingEndAreBounded()
    {
        var reader = new FitsReader(maximumHeaderBytes: 2880);
        using var invalid = new MemoryStream(Image(8, [], width: -1));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadHeaderAsync(invalid));
        var bytes = Image(8, new Byte[2880]); Array.Fill(bytes, (Byte)' ', 400, 2480);
        using var noEnd = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadHeaderAsync(noEnd));
    }

    [Fact]
    public async Task ValidationAndCancellationLeaveReaderReusable()
    {
        var reader = new FitsReader(); using var stream = new MemoryStream(Image(8, [1, 2]));
        var header = await reader.ReadHeaderAsync(stream);
        await Assert.ThrowsAsync<ArgumentException>(async () => await reader.ReadPixelsAsync<Byte>(stream, header, new Byte[1]));
        Assert.Equal(2880, stream.Position);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reader.ReadPixelsAsync<Byte>(stream, header, new Byte[2], cancellationToken: new CancellationToken(true)));
        stream.Position = 2880;
        await reader.ReadPixelsAsync<Byte>(stream, header, new Byte[2]);
    }

    [Fact]
    public async Task RepeatedPixelReadsAllocateNoFrameSizedStorage()
    {
        var payload = new Byte[1024 * 1024];
        using var stream = new MemoryStream(Image(8, payload, width: 1024, height: 1024));
        var reader = new FitsReader(); var header = await reader.ReadHeaderAsync(stream); var output = new Byte[payload.Length];
        await reader.ReadPixelsAsync<Byte>(stream, header, output);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 8; i++) { stream.Position = 2880; await reader.ReadPixelsAsync<Byte>(stream, header, output); }
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16384);
    }

    [Fact]
    public async Task NonCanonicalUInt64OffsetDoesNotSilentlyRoundToUnsignedConvention()
    {
        using var stream = new MemoryStream(Image(64, new Byte[16], ["BZERO   = 9223372036854775809"]));
        var reader = new FitsReader(); var header = await reader.ReadHeaderAsync(stream);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await reader.ReadPixelsAsync<UInt64>(stream, header, new UInt64[2]));
        Assert.Equal(2880, stream.Position);
    }

    [Fact]
    public async Task StoredDoubleBypassesScalingAndPhysicalDoubleAppliesIt()
    {
        await Check<Double>(-64, [63, 248, 0, 0, 0, 0, 0, 0, 192, 2, 0, 0, 0, 0, 0, 0], [13, 5.5],
            ["BSCALE  = 2", "BZERO   = 10"]);
        await Check<Double>(-64, [63, 248, 0, 0, 0, 0, 0, 0, 192, 2, 0, 0, 0, 0, 0, 0], [1.5, -2.25],
            ["BSCALE  = 2", "BZERO   = 10"], FitsPixelValueMode.Stored);
    }

    [Fact]
    public async Task EmptyPrimaryAndMultidimensionalImageAreSupported()
    {
        var empty = new Byte[2880]; Array.Fill(empty, (Byte)' ');
        String[] cards = ["SIMPLE  = T", "BITPIX  = 8", "NAXIS   = 0", "END"];
        for (var i = 0; i < cards.Length; i++) Encoding.ASCII.GetBytes(cards[i], empty.AsSpan(i * 80));
        var reader = new FitsReader(); using var stream = new MemoryStream(empty);
        var header = await reader.ReadHeaderAsync(stream);
        Assert.Empty(header.Axes); Assert.Equal(0, header.PixelCount);
        await reader.ReadPixelsAsync<Byte>(stream, header, Memory<Byte>.Empty);
        var cube = Image(8, [1, 2, 3, 4, 5, 6, 7, 8]);
        Array.Fill(cube, (Byte)' ', 240, 2640);
        Encoding.ASCII.GetBytes("NAXIS   = 3".PadRight(80), cube.AsSpan(160));
        Encoding.ASCII.GetBytes("NAXIS2  = 2".PadRight(80), cube.AsSpan(320));
        Encoding.ASCII.GetBytes("NAXIS3  = 2".PadRight(80), cube.AsSpan(400));
        Encoding.ASCII.GetBytes("NAXIS1  = 2".PadRight(80), cube.AsSpan(240));
        Encoding.ASCII.GetBytes("END".PadRight(80), cube.AsSpan(480));
        using var cubeStream = new MemoryStream(cube);
        header = await reader.ReadHeaderAsync(cubeStream);
        Assert.Equal(new Int64[] { 2, 2, 2 }, header.Axes);
        var output = new Byte[8]; await reader.ReadPixelsAsync<Byte>(cubeStream, header, output);
        Assert.Equal(new Byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, output);
    }

    [Fact]
    public async Task RegionValidationDoesNotConsumePixels()
    {
        var reader = new FitsReader(); using var stream = new FragmentedStream(Image(8, [1, 2]));
        var header = await reader.ReadHeaderAsync(stream);
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await reader.ReadRegionAsync<Byte>(stream, header, new FitsRegion(0, 0, 1, 1), new Byte[1], FitsRegionReadMode.Seek));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await reader.ReadRegionAsync<Byte>(stream, header, new FitsRegion(1, 0, 2, 1), new Byte[2]));
        var output = new Byte[2]; await reader.ReadPixelsAsync<Byte>(stream, header, output);
        Assert.Equal(new Byte[] { 1, 2 }, output);
    }

    private sealed class FragmentedStream(Byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override Boolean CanRead => true;
        public override Boolean CanSeek => false;
        public override Boolean CanWrite => false;
        public override Int64 Length => throw new NotSupportedException();
        public override Int64 Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override Int32 Read(Byte[] buffer, Int32 offset, Int32 count) => _inner.Read(buffer, offset, Math.Min(count, 7));
        public override ValueTask<Int32> ReadAsync(Memory<Byte> buffer, CancellationToken token = default) => _inner.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], token);
        public override void Flush() => throw new NotSupportedException();
        public override Int64 Seek(Int64 offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(Int64 value) => throw new NotSupportedException();
        public override void Write(Byte[] buffer, Int32 offset, Int32 count) => throw new NotSupportedException();
        protected override void Dispose(Boolean disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}

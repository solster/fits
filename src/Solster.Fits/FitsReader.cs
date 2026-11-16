using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Solster.Fits;

/// <summary>Reusable, non-concurrent workspace. Streams and destination buffers belong to the caller.
/// Pixel operations start at the payload and finish before padding. On failure the stream may be
/// partially consumed. A streaming callback must finish using its memory before it completes.</summary>
public sealed class FitsReader
{
    private readonly Byte[] _buffer;
    private readonly Int32 _maximumHeaderBytes;
    private Int32 _busy;

    public FitsReader(Int32 bufferSize = 65536, Int32 maximumHeaderBytes = 16 * 1024 * 1024)
    {
        if (bufferSize < 2880) throw new ArgumentOutOfRangeException(nameof(bufferSize));
        if (maximumHeaderBytes < 2880) throw new ArgumentOutOfRangeException(nameof(maximumHeaderBytes));
        _buffer = new Byte[bufferSize];
        _maximumHeaderBytes = maximumHeaderBytes;
    }

    public async ValueTask<FitsImageHeader> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var hdu = await ReadHduAsync(stream, cancellationToken);
        return hdu switch
        {
            FitsImageHeader image => image,
            FitsUnsupportedHdu other => throw new NotSupportedException($"Unsupported FITS HDU: {other.ExtensionType}."),
            null => throw new EndOfStreamException("No FITS header remains.")
        };
    }

    /// <summary>Reads the next header at the current HDU boundary. A default union means clean EOF.
    /// Unsupported HDUs can be skipped using their payload length. Does not scan pixels.</summary>
    public async ValueTask<FitsHdu> ReadHduAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        Enter();
        try
        {
            var first = await stream.ReadAsync(_buffer.AsMemory(0, 1), cancellationToken);
            if (first == 0) return default;
            await stream.ReadExactlyAsync(_buffer.AsMemory(1, 2879), cancellationToken);
            using var cards = new MemoryStream();
            Int32 cardCount = 0;
            var ended = false;
            do
            {
                if (cards.Length + 2880 > _maximumHeaderBytes)
                    throw new InvalidDataException("FITS header exceeds the configured limit.");
                for (var i = 0; i < 2880; i += 80)
                {
                    var card = _buffer.AsSpan(i, 80);
                    foreach (var b in card)
                        if (b < 32 || b > 126) throw new InvalidDataException("Non-ASCII FITS header.");
                    cardCount++;
                    if (card[..8].SequenceEqual("END     "u8))
                    {
                        if (card[8..].IndexOfAnyExcept((Byte)' ') >= 0 ||
                            _buffer.AsSpan(i + 80, 2880 - i - 80).IndexOfAnyExcept((Byte)' ') >= 0)
                            throw new InvalidDataException("Invalid END card or header padding.");
                        ended = true;
                        break;
                    }
                }
                cards.Write(_buffer, 0, 2880);
                if (!ended) await stream.ReadExactlyAsync(_buffer.AsMemory(0, 2880), cancellationToken);
            } while (!ended);
            return ParseHeader(cards.ToArray(), cardCount);
        }
        finally { Exit(); }
    }

    private static FitsHdu ParseHeader(Byte[] cards, Int32 cardCount)
    {
        var raw = new FitsHeader(cards, cardCount, cards.Length, 0, [], 0);
        var first = Encoding.ASCII.GetString(cards, 0, 8).TrimEnd();
        Boolean primary;
        String extension;
        if (first == "SIMPLE" && raw.TryGetString("SIMPLE", out var simple) && simple == "T")
        { primary = true; extension = "PRIMARY"; }
        else if (first == "XTENSION" && raw.TryGetString("XTENSION", out var xtension))
        { primary = false; extension = xtension!; }
        else throw new InvalidDataException("Expected SIMPLE = T or XTENSION at the start of an HDU.");
        Int64 Required(String key) => raw.TryGetInt64(key, out var n) ? n :
            throw new InvalidDataException($"Missing or invalid {key}.");
        var bitPix = Required("BITPIX");
        if (bitPix is not (8 or 16 or 32 or 64 or -32 or -64))
            throw new InvalidDataException("Invalid BITPIX.");
        var rank = Required("NAXIS");
        if (rank < 0 || rank > 999) throw new InvalidDataException("Invalid NAXIS.");
        // Required structural keywords must occur in standard order.
        void Expect(Int32 index, String key)
        {
            if (index >= cardCount || Encoding.ASCII.GetString(cards, index * 80, 8).TrimEnd() != key)
                throw new InvalidDataException($"Expected {key} at card {index + 1}.");
        }
        Expect(1, "BITPIX"); Expect(2, "NAXIS");
        var axes = new Int64[(Int32)rank];
        Int64 count = rank == 0 ? 0 : 1;
        try
        {
            for (var i = 0; i < axes.Length; i++)
            {
                var key = $"NAXIS{i + 1}";
                Expect(3 + i, key);
                axes[i] = Required(key);
                if (axes[i] < 0) throw new InvalidDataException("Negative axis length.");
                count = checked(count * axes[i]);
            }
            if (!primary) { Expect(3 + axes.Length, "PCOUNT"); Expect(4 + axes.Length, "GCOUNT"); }
            var pcount = primary ? 0 : Required("PCOUNT");
            var gcount = primary ? 1 : Required("GCOUNT");
            if (pcount < 0 || gcount < 1) throw new InvalidDataException("Invalid group dimensions.");
            var groups = raw.TryGetString("GROUPS", out var grouped) && grouped == "T";
            if (groups)
            {
                pcount = Required("PCOUNT"); gcount = Required("GCOUNT");
                if (!primary || axes.Length < 2 || axes[0] != 0 || pcount < 0 || gcount < 1)
                    throw new InvalidDataException("Invalid random groups HDU.");
                count = 1;
                for (var i = 1; i < axes.Length; i++) count = checked(count * axes[i]);
            }
            var bytes = checked((count + pcount) * gcount * (Math.Abs(bitPix) / 8));
            var header = new FitsHeader(cards, cardCount, cards.Length, (Int32)bitPix, axes, bytes);
            if (groups || extension is not ("PRIMARY" or "IMAGE"))
                return new FitsUnsupportedHdu(header, groups ? "Random groups" : extension);
            if (pcount != 0 || gcount != 1) throw new InvalidDataException("Invalid image PCOUNT/GCOUNT.");
            Double Number(String key, Double fallback)
            {
                if (!raw.TryGetString(key, out _)) return fallback;
                if (!raw.TryGetDouble(key, out var n)) throw new InvalidDataException($"Invalid {key}.");
                return n;
            }
            Int64? blank = null;
            if (raw.TryGetString("BLANK", out _))
            {
                blank = Required("BLANK");
                if (bitPix < 0 || bitPix == 8 && (blank < 0 || blank > 255) ||
                    bitPix == 16 && (blank < Int16.MinValue || blank > Int16.MaxValue) ||
                    bitPix == 32 && (blank < Int32.MinValue || blank > Int32.MaxValue))
                    throw new InvalidDataException("Invalid BLANK value.");
            }
            return new FitsImageHeader(cards, cardCount, cards.Length, (Int32)bitPix, axes, bytes,
                Number("BSCALE", 1), Number("BZERO", 0), blank);
        }
        catch (OverflowException ex) { throw new InvalidDataException("FITS dimensions overflow supported lengths.", ex); }
    }

    public async ValueTask ReadPixelsAsync<T>(Stream stream, FitsImageHeader header, Memory<T> destination,
        FitsPixelValueMode values = FitsPixelValueMode.Physical, CancellationToken cancellationToken = default)
        where T : unmanaged, INumberBase<T>
    {
        if (destination.Length < header.PixelCount) throw new ArgumentException("Destination is too small.", nameof(destination));
        Enter();
        try
        {
            ValidateType<T>(header, values);
            for (Int64 offset = 0; offset < header.PixelCount;)
            {
                var count = (Int32)Math.Min(_buffer.Length / BytesPerPixel(header), header.PixelCount - offset);
                await stream.ReadExactlyAsync(_buffer.AsMemory(0, count * BytesPerPixel(header)), cancellationToken);
                Decode(header, destination.Span.Slice((Int32)offset, count), values);
                offset += count;
            }
        }
        finally { Exit(); }
    }

    public async ValueTask StreamPixelsAsync<T>(Stream stream, FitsImageHeader header, Memory<T> scratch,
        FitsPixelConsumer<T> consumer, FitsPixelValueMode values = FitsPixelValueMode.Physical,
        CancellationToken cancellationToken = default) where T : unmanaged, INumberBase<T>
    {
        if (scratch.IsEmpty) throw new ArgumentException("Scratch memory must not be empty.", nameof(scratch));
        ArgumentNullException.ThrowIfNull(consumer);
        Enter();
        try
        {
            ValidateType<T>(header, values);
            for (Int64 offset = 0; offset < header.PixelCount;)
            {
                var count = (Int32)Math.Min(Math.Min(scratch.Length, _buffer.Length / BytesPerPixel(header)), header.PixelCount - offset);
                await stream.ReadExactlyAsync(_buffer.AsMemory(0, count * BytesPerPixel(header)), cancellationToken);
                Decode(header, scratch.Span[..count], values);
                await consumer(scratch[..count], offset, cancellationToken);
                offset += count;
            }
        }
        finally { Exit(); }
    }

    public async ValueTask ReadRegionAsync<T>(Stream stream, FitsImageHeader header, FitsRegion region,
        Memory<T> destination, FitsRegionReadMode mode = FitsRegionReadMode.Sequential,
        FitsPixelValueMode values = FitsPixelValueMode.Physical, CancellationToken cancellationToken = default)
        where T : unmanaged, INumberBase<T>
    {
        if (header.Axes.Count != 2) throw new NotSupportedException("Rectangle reads require a two-dimensional image.");
        var width = header.Axes[0]; var height = header.Axes[1];
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.X > width - region.Width || region.Y > height - region.Height)
            throw new ArgumentOutOfRangeException(nameof(region));
        var size = checked((Int64)region.Width * region.Height);
        if (destination.Length < size) throw new ArgumentException("Destination is too small.", nameof(destination));
        if (mode is not (FitsRegionReadMode.Sequential or FitsRegionReadMode.Seek)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == FitsRegionReadMode.Seek && !stream.CanSeek) throw new NotSupportedException("Seek region reads require a seekable stream.");
        Enter();
        try
        {
            ValidateType<T>(header, values);
            var bpp = BytesPerPixel(header);
            var origin = mode == FitsRegionReadMode.Seek ? stream.Position : 0;
            Int64 position = 0;
            for (var row = 0; row < region.Height; row++)
            {
                var start = checked(((region.Y + (Int64)row) * width + region.X) * bpp);
                if (mode == FitsRegionReadMode.Seek) stream.Seek(checked(origin + start), SeekOrigin.Begin);
                else await DiscardAsync(stream, start - position, cancellationToken);
                for (var column = 0; column < region.Width;)
                {
                    var count = Math.Min(_buffer.Length / bpp, region.Width - column);
                    await stream.ReadExactlyAsync(_buffer.AsMemory(0, count * bpp), cancellationToken);
                    Decode(header, destination.Span.Slice(row * region.Width + column, count), values);
                    column += count;
                }
                position = start + (Int64)region.Width * bpp;
            }
            if (mode == FitsRegionReadMode.Sequential)
                await DiscardAsync(stream, header.PayloadByteLength - position, cancellationToken);
            else stream.Seek(checked(origin + header.PayloadByteLength), SeekOrigin.Begin);
        }
        finally { Exit(); }
    }

    /// <summary>Call at the payload start to reach the next HDU. Reads skipped bytes to detect truncation.</summary>
    public async ValueTask SkipHduAsync(Stream stream, FitsHeader header, CancellationToken cancellationToken = default)
    {
        Enter();
        try { await DiscardAsync(stream, checked(header.PayloadByteLength + header.PaddingByteLength), cancellationToken); }
        finally { Exit(); }
    }

    /// <summary>Call after a complete pixel/region operation to reach the next HDU boundary.</summary>
    public async ValueTask SkipPaddingAsync(Stream stream, FitsHeader header, CancellationToken cancellationToken = default)
    {
        Enter();
        try { await DiscardAsync(stream, header.PaddingByteLength, cancellationToken); }
        finally { Exit(); }
    }

    private async ValueTask DiscardAsync(Stream stream, Int64 bytes, CancellationToken token)
    {
        while (bytes > 0)
        {
            var count = (Int32)Math.Min(_buffer.Length, bytes);
            await stream.ReadExactlyAsync(_buffer.AsMemory(0, count), token);
            bytes -= count;
        }
    }

    private static Int32 BytesPerPixel(FitsImageHeader header) => Math.Abs(header.BitPix) / 8;
    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Use a separate FITS reader for each concurrent operation.");
    }
    private void Exit() => Volatile.Write(ref _busy, 0);

    private static void ValidateType<T>(FitsImageHeader h, FitsPixelValueMode mode) where T : unmanaged, INumberBase<T>
    {
        if (mode is not (FitsPixelValueMode.Physical or FitsPixelValueMode.Stored)) throw new ArgumentOutOfRangeException(nameof(mode));
        var physical = mode == FitsPixelValueMode.Physical;
        if (physical && typeof(T) == typeof(Double)) return; // Explicit, potentially lossy physical conversion.
        var zero = physical ? h.BZero : 0;
        if (physical && h.BScale != 1) throw new NotSupportedException("Scaled pixels require Double output.");
        var supported = h.BitPix switch
        {
            8 => zero == 0 && typeof(T) == typeof(Byte),
            16 => zero == 0 && typeof(T) == typeof(Int16) || physical && h.IsUnsigned && typeof(T) == typeof(UInt16),
            32 => zero == 0 && typeof(T) == typeof(Int32) || physical && h.IsUnsigned && typeof(T) == typeof(UInt32),
            64 => zero == 0 && typeof(T) == typeof(Int64) || physical && h.IsUnsigned && typeof(T) == typeof(UInt64),
            -32 => zero == 0 && typeof(T) == typeof(Single),
            -64 => zero == 0 && typeof(T) == typeof(Double),
            _ => false
        };
        if (!supported) throw new NotSupportedException("Output must match the stored/native physical type, or be Double for physical conversion.");
    }

    private void Decode<T>(FitsImageHeader h, Span<T> output, FitsPixelValueMode mode) where T : unmanaged, INumberBase<T>
    {
        // Select the stored representation once per chunk. Generic type checks below are JIT constants.
        switch (h.BitPix)
        {
            case 8: DecodeCore<Byte, T>(h, output, mode); break;
            case 16: DecodeCore<Int16, T>(h, output, mode); break;
            case 32: DecodeCore<Int32, T>(h, output, mode); break;
            case 64: DecodeCore<Int64, T>(h, output, mode); break;
            case -32: DecodeCore<Single, T>(h, output, mode); break;
            case -64: DecodeCore<Double, T>(h, output, mode); break;
        }
    }

    private void DecodeCore<TStored, T>(FitsImageHeader h, Span<T> output, FitsPixelValueMode mode)
        where TStored : unmanaged, INumberBase<TStored> where T : unmanaged, INumberBase<T>
    {
        var bytes = _buffer.AsSpan();
        var size = Marshal.SizeOf<TStored>();
        for (var i = 0; i < output.Length; i++)
        {
            var source = bytes.Slice(i * size, size);
            TStored stored;
            if (typeof(TStored) == typeof(Byte)) stored = TStored.CreateChecked(source[0]);
            else if (typeof(TStored) == typeof(Int16)) stored = TStored.CreateChecked(BinaryPrimitives.ReadInt16BigEndian(source));
            else if (typeof(TStored) == typeof(Int32)) stored = TStored.CreateChecked(BinaryPrimitives.ReadInt32BigEndian(source));
            else if (typeof(TStored) == typeof(Int64)) stored = TStored.CreateChecked(BinaryPrimitives.ReadInt64BigEndian(source));
            else if (typeof(TStored) == typeof(Single)) stored = TStored.CreateChecked(BinaryPrimitives.ReadSingleBigEndian(source));
            else stored = TStored.CreateChecked(BinaryPrimitives.ReadDoubleBigEndian(source));
            if (mode == FitsPixelValueMode.Stored) { output[i] = T.CreateChecked(stored); continue; }
            var blank = h.Blank is { } sentinel && h.BitPix > 0 && Int64.CreateChecked(stored) == sentinel;
            if (blank)
            {
                if (typeof(T) != typeof(Double) && typeof(T) != typeof(Single))
                    throw new InvalidDataException("BLANK cannot be represented by an integral output buffer. Use floating physical output or stored values.");
                output[i] = T.CreateChecked(Double.NaN);
            }
            else if (typeof(T) == typeof(Double))
                output[i] = T.CreateChecked(Double.CreateChecked(stored) * h.BScale + h.BZero);
            else if (typeof(T) == typeof(UInt64))
                output[i] = T.CreateChecked(unchecked((UInt64)Int64.CreateChecked(stored)) ^ 0x8000000000000000UL);
            else if (typeof(T) == typeof(UInt32))
                output[i] = T.CreateChecked((UInt32)(Int64.CreateChecked(stored) + 2147483648L));
            else if (typeof(T) == typeof(UInt16))
                output[i] = T.CreateChecked((UInt16)(Int64.CreateChecked(stored) + 32768));
            else output[i] = T.CreateChecked(stored);
        }
    }
}

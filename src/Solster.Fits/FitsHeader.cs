using System.Globalization;
using System.Text;

namespace Solster.Fits;

/// <summary>Owned header bytes, preserved in card order including duplicate commentary.
/// HeaderByteLength is relative to the start of this HDU, not an absolute stream offset.</summary>
public class FitsHeader
{
    private readonly Byte[] _cards;
    internal FitsHeader(Byte[] cards, Int32 cardCount, Int64 headerByteLength,
        Int32 bitPix, Int64[] axes, Int64 payloadByteLength)
    {
        _cards = cards;
        CardCount = cardCount;
        HeaderByteLength = headerByteLength;
        BitPix = bitPix;
        Axes = Array.AsReadOnly(axes);
        PayloadByteLength = payloadByteLength;
    }

    public Int32 CardCount { get; }
    public Int64 HeaderByteLength { get; }
    public Int32 BitPix { get; }
    public IReadOnlyList<Int64> Axes { get; }
    public Int64 PayloadByteLength { get; }
    public Int64 PaddingByteLength => (2880 - PayloadByteLength % 2880) % 2880;

    public ReadOnlyMemory<Byte> GetCard(Int32 index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if (index >= CardCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _cards.AsMemory(index * 80, 80);
    }

    public Boolean TryGetString(String keyword, out String? value)
    {
        value = null;
        Span<Char> name = stackalloc Char[8];
        Span<Char> result = stackalloc Char[70];
        for (var i = CardCount - 1; i >= 0; i--)
        {
            var card = _cards.AsSpan(i * 80, 80);
            Encoding.ASCII.GetChars(card[..8], name);
            if (!name.TrimEnd().Equals(keyword, StringComparison.OrdinalIgnoreCase)
                || card[8] != '=' || card[9] != ' ') continue;
            var text = Encoding.ASCII.GetString(card[10..]).AsSpan().TrimStart();
            if (text.StartsWith("'"))
            {
                var length = 0;
                for (var j = 1; j < text.Length; j++)
                {
                    if (text[j] == '\'')
                    {
                        if (j + 1 < text.Length && text[j + 1] == '\'') { result[length++] = '\''; j++; }
                        else { value = result[..length].TrimEnd().ToString(); return true; }
                    }
                    else result[length++] = text[j];
                }
                throw new InvalidDataException($"Unterminated FITS string: {keyword}.");
            }
            var comment = text.IndexOf('/');
            value = (comment < 0 ? text : text[..comment]).Trim().ToString();
            return true;
        }
        return false;
    }

    public Boolean TryGetInt64(String keyword, out Int64 value)
    {
        value = 0;
        return TryGetString(keyword, out var text) && Int64.TryParse(text,
            NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public Boolean TryGetDouble(String keyword, out Double value)
    {
        value = 0;
        return TryGetString(keyword, out var text) && Double.TryParse(text!.Replace('D', 'E'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Double.IsFinite(value);
    }
}

public sealed class FitsImageHeader : FitsHeader
{
    internal FitsImageHeader(Byte[] cards, Int32 cardCount, Int64 headerByteLength,
        Int32 bitPix, Int64[] axes, Int64 payloadByteLength, Double scale, Double zero, Int64? blank)
        : base(cards, cardCount, headerByteLength, bitPix, axes, payloadByteLength)
    {
        PixelCount = axes.Length == 0 ? 0 : axes.Aggregate(1L, (a, b) => checked(a * b));
        BScale = scale;
        BZero = zero;
        Blank = blank;
        IsUnsigned = bitPix switch
        {
            16 => HasExactZero(32768m),
            32 => HasExactZero(2147483648m),
            64 => HasExactZero(9223372036854775808m),
            _ => false
        };
    }

    internal Boolean IsUnsigned { get; }

    private Boolean HasExactZero(Decimal expected) => TryGetString("BZERO", out var text) &&
        Decimal.TryParse(text!.Replace('D', 'E'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value == expected;

    public Int64 PixelCount { get; }
    public Double BScale { get; }
    public Double BZero { get; }
    public Int64? Blank { get; }
}

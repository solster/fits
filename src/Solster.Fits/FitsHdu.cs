namespace Solster.Fits;

/// <summary>A header describes an image or an HDU whose payload this library cannot decode.</summary>
public union FitsHdu(FitsImageHeader, FitsUnsupportedHdu);

public sealed record FitsUnsupportedHdu(FitsHeader Header, String ExtensionType);

public readonly record struct FitsRegion(Int32 X, Int32 Y, Int32 Width, Int32 Height);

public enum FitsRegionReadMode
{
    Sequential,
    Seek
}

public enum FitsPixelValueMode
{
    /// <summary>Apply BSCALE/BZERO. Integer BLANK becomes NaN for floating output,
    /// or throws for integral output. Native unsigned offsets are exact.</summary>
    Physical,
    /// <summary>Return the stored values, including BLANK sentinels, without scaling.</summary>
    Stored
}

public delegate ValueTask FitsPixelConsumer<T>(ReadOnlyMemory<T> pixels, Int64 pixelOffset,
    CancellationToken cancellationToken) where T : unmanaged;

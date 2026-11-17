# Solster.Fits

.NET 11 / C# 15 FITS image I/O focused on caller-owned buffers and reusable workspaces.
No dependencies on astrophotography applications, statistics, display, or filesystem adapters.
Licensed under AGPL-3.0-only; see LICENSE.

## Supported scope

The implementation follows [FITS Standard 4.0](https://fits.gsfc.nasa.gov/standard40/fits_standard40aa.pdf)
for uncompressed primary arrays and IMAGE extensions: BITPIX 8, 16, 32, 64, -32 and -64,
big-endian pixels, arbitrary image dimensions, BSCALE/BZERO, integer BLANK and floating NaN.
This is an image reader, not an implementation of every FITS feature or a full-file validator.
Table, tile-compressed and random-group payload decoding is unsupported. HDU discovery identifies
unsupported extensions and allows bounded-memory skipping to the next HDU.
Writers, CHECKSUM verification, HIERARCH interpretation and long-string CONTINUE assembly are not implemented.
Original header cards remain available in order, including repeated COMMENT/HISTORY and unrecognized cards.

## Ownership and API

- `FitsReader` owns one reusable byte buffer. It has no pooled resources and needs no disposal.
  Overlapping operations on one reader are rejected; parallel operations use separate readers.
- The caller owns streams and pixel/scratch buffers. Streams are never closed.
- `ReadHeaderAsync` leaves the stream at the image payload. Headers own their bytes and survive reader reuse.
  Header lengths are relative to the HDU start; they are not absolute source offsets.
- `ReadHduAsync` returns a C# union of `FitsImageHeader` and `FitsUnsupportedHdu`.
  Null union contents indicate clean EOF; a partial header throws. The caller can pattern-match each case.
- `ReadPixelsAsync<T>` writes a complete image into caller memory. `StreamPixelsAsync<T>` writes bounded
  chunks into caller scratch and awaits the consumer before reusing it. Chunks may cross rows;
  offsets count pixels from the image start. Copy a chunk if it must outlive the consumer callback.
- `ReadRegionAsync<T>` writes a tightly packed two-dimensional rectangle in stored row order.
  Sequential mode reads the complete payload and detects truncation outside the rectangle.
  Seek mode requires a seekable stream, reads selected row segments, and does not validate unvisited bytes.
  Both finish at the payload end. Coordinates start at zero and are not display-flipped.
- All pixel/region methods start at the payload, finish before FITS padding, and support cancellation.
  Call `SkipPaddingAsync` afterward, or `SkipHduAsync` at the payload start, before reading another header.
  Skip methods consume bytes to detect truncation. Failure/cancellation can leave partial output and stream progress;
  reopen or explicitly reposition before retrying.
- Destination lengths and type compatibility are checked before pixels are consumed. Extra destination elements are untouched.

## Numeric semantics

Physical values are the default: stored value times BSCALE plus BZERO. With unit scale, native signed
and floating types are preserved. Standard unsigned 16/32/64-bit offsets decode exactly into UInt16/UInt32/UInt64.
For other scaling, request Double output explicitly. This can round large integers (including values above 2^53)
and can overflow to floating infinity; there is no implicit clipping or narrowing conversion.
Physical integer BLANK values become NaN for floating output and throw for integral output.
Stored mode bypasses scaling and returns the original signed type (or Byte/Single/Double), including BLANK sentinels.
Metadata keyword lookup is case-insensitive (last value wins for duplicate value cards); structural keyword order is validated. String lookup removes FITS trailing
padding, preserves leading spaces and unescapes doubled quotes. Numeric parsing is culture-independent and accepts D exponents.
Headers are bounded to 16 MiB by default; configure that limit and the read buffer size in the reader constructor.
Pixel counts and payload lengths use checked 64-bit arithmetic. Full-image destinations are limited by .NET memory sizes;
streaming can process larger arrays without allocating a full frame.

## Build and verification

Install .NET SDK 11.0.100 or a newer .NET 11 feature band.

```sh
dotnet restore Fits.slnx
dotnet build Fits.slnx --no-restore
dotnet test Fits.slnx --no-build
dotnet run -c Release --project benchmarks/Solster.Fits.Benchmarks
dotnet pack src/Solster.Fits -c Release -o artifacts
```

The dependency-free benchmark harness uses a synthetic 4096 x 4096 UInt16 frame in memory.
It reports warmed elapsed time, decoded/read MiB/s and managed allocation per operation for headers,
full images, streaming, and both region modes. It measures decoding rather than disk throughput;
results depend on hardware, runtime and tiered compilation. Allocations are measured across the process.

## GitHub Packages

Package ID: `Solster.Fits`. Feed: `https://nuget.pkg.github.com/solster/index.json`.
The `Publish` GitHub Actions workflow builds, tests, packs and publishes using a repository-scoped
GITHUB_TOKEN with packages:write. Run it manually after changing the project Version; versions are immutable.
The package is public and linked to the public `solster/fits` repository. The workflow verifies public visibility.
Local consumers need a GitHub credential with read:packages and access to the package.
Keep credentials outside version control, for example through NuGet's `NuGetPackageSourceCredentials_github`
environment variable or a credential provider. The astrophotography repo configures source mapping for this package.

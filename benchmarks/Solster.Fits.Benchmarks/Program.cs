using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Solster.Fits;

const Int32 width = 4096, height = 4096;
var bytes = new Byte[2880 + width * height * 2];
Array.Fill(bytes, (Byte)' ', 0, 2880);
String[] cards = ["SIMPLE  = T", "BITPIX  = 16", "NAXIS   = 2", $"NAXIS1  = {width}", $"NAXIS2  = {height}", "BZERO   = 32768", "END"];
for (var i = 0; i < cards.Length; i++) Encoding.ASCII.GetBytes(cards[i], bytes.AsSpan(i * 80));
for (var i = 2880; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i), (Int16)(i % 32768));
using var stream = new MemoryStream(bytes, false);
var reader = new FitsReader(); var header = await reader.ReadHeaderAsync(stream);
var output = new UInt16[width * height]; var scratch = new UInt16[32768]; var patch = new UInt16[200 * 200];
UInt64 checksum = 0;
await Measure("Header", 2000, 2880, async () => { stream.Position = 0; await reader.ReadHeaderAsync(stream); });
await Measure("Full UInt16", 20, header.PayloadByteLength, async () => { stream.Position = 2880; await reader.ReadPixelsAsync<UInt16>(stream, header, output); checksum += output[0]; });
await Measure("Streaming UInt16", 20, header.PayloadByteLength, async () =>
{
    stream.Position = 2880;
    await reader.StreamPixelsAsync<UInt16>(stream, header, scratch, (pixels, offset, token) => { checksum += pixels.Span[0]; return ValueTask.CompletedTask; });
});
await Measure("Sequential region", 20, header.PayloadByteLength, async () => { stream.Position = 2880; await reader.ReadRegionAsync<UInt16>(stream, header, new FitsRegion(1900, 1900, 200, 200), patch); checksum += patch[0]; });
await Measure("Seek region", 200, 200 * 200 * 2, async () => { stream.Position = 2880; await reader.ReadRegionAsync<UInt16>(stream, header, new FitsRegion(1900, 1900, 200, 200), patch, FitsRegionReadMode.Seek); checksum += patch[0]; });
Console.WriteLine($"Checksum: {checksum}");

static async Task Measure(String name, Int32 iterations, Int64 bytes, Func<ValueTask> action)
{
    for (var i = 0; i < 10; i++) await action();
    var allocated = GC.GetTotalAllocatedBytes(true); var timer = Stopwatch.StartNew();
    for (var i = 0; i < iterations; i++) await action();
    timer.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    Console.WriteLine($"{name}: {timer.Elapsed.TotalMilliseconds / iterations:F3} ms/op, {bytes * iterations / timer.Elapsed.TotalSeconds / (1024 * 1024):F1} MiB/s, {allocated / (Double)iterations:F0} B/op");
}

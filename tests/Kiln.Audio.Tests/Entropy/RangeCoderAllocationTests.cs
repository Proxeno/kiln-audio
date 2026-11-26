using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests;

public class RangeCoderAllocationTests
{
    [Fact]
    public void Hot_decode_paths_allocate_nothing()
    {
        var frame = new byte[512];
        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] = (byte)(i * 37 + 11);
        }

        byte[] table = [255, 0];

        // Warm up so that any one-time JIT allocations happen before measurement.
        _ = ConsumeHotPaths(frame, table);
        _ = ConsumeHotPaths(frame, table);
        _ = ConsumeHotPaths(frame, table);

        long before = GC.GetAllocatedBytesForCurrentThread();
        uint result = ConsumeHotPaths(frame, table);
        long after = GC.GetAllocatedBytesForCurrentThread();

        GC.KeepAlive(result);
        Assert.Equal(before, after);
    }

    private static uint ConsumeHotPaths(byte[] frame, byte[] table)
    {
        var decoder = new RangeDecoder(frame);
        uint accumulator = 0;
        for (int i = 0; i < 1000; i++)
        {
            // Decode + Update (RFC 6716 §4.1.2).
            uint fs = decoder.Decode(16);
            decoder.Update(fs, fs + 1, 16);
            accumulator += fs;

            // DecodeIcdf (RFC 6716 §4.1.3.3).
            accumulator += (uint)decoder.DecodeIcdf(table, 8);

            // DecodeBits (RFC 6716 §4.1.4).
            accumulator += decoder.DecodeBits(8);
        }

        return accumulator;
    }
}

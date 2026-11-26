using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests;

public class RangeCoderBufferTests
{
    [Fact]
    public void Exact_size_succeeds_and_one_byte_short_sets_error()
    {
        List<RangeOp> ops = RangeCoderTestStreams.Generate(new Random(12345));

        var probeBuffer = new byte[1 << 16];
        var probe = new RangeEncoder(probeBuffer);
        foreach (RangeOp op in ops)
        {
            RangeCoderTestStreams.ApplyEncode(ref probe, op);
        }

        probe.Done();
        Assert.False(probe.Error);
        int needed = probe.RangeBytes;
        Assert.True(needed > 1);

        var exactBuffer = new byte[needed];
        var exact = new RangeEncoder(exactBuffer);
        foreach (RangeOp op in ops)
        {
            RangeCoderTestStreams.ApplyEncode(ref exact, op);
        }

        exact.Done();
        Assert.False(exact.Error);
        Assert.Equal(needed, exact.RangeBytes);

        var shortBuffer = new byte[needed - 1];
        var shortEncoder = new RangeEncoder(shortBuffer);
        foreach (RangeOp op in ops)
        {
            RangeCoderTestStreams.ApplyEncode(ref shortEncoder, op);
        }

        // Finalizing a too-small buffer must set Error, never throw.
        shortEncoder.Done();
        Assert.True(shortEncoder.Error);
    }

    [Fact]
    public void Empty_frame_decodes_without_throwing_and_reports_one_bit()
    {
        var decoder = new RangeDecoder(ReadOnlySpan<byte>.Empty);
        Assert.False(decoder.Error);
        Assert.Equal(1, decoder.Tell());
        Assert.Equal(8u, decoder.TellFrac());
        Assert.Equal(RangeDecoder.EcCodeTop, decoder.Rng);
        Assert.Equal(0u, decoder.DecodeBits(8));
    }

    [Fact]
    public void One_byte_frame_decodes_without_throwing()
    {
        var decoder = new RangeDecoder(new byte[] { 0x80 });
        Assert.Equal(2u, decoder.Decode(4));
        decoder.Update(2, 3, 4);
        Assert.Equal(0u, decoder.DecodeBits(1));
        Assert.False(decoder.Error);
    }

    [Fact]
    public void UInt_outside_range_sets_error_and_saturates()
    {
        // With a single 0xFF byte the top eight bits decode to their maximum and the two raw
        // bits are ones, so ft = 1001 yields 1003.
        var decoder = new RangeDecoder(new byte[] { 0xFF });
        uint value = decoder.DecodeUInt(1001);
        Assert.True(decoder.Error);
        Assert.Equal(1000u, value);
    }

    [Fact]
    public void Icdf_table_terminated_by_zero_round_trips()
    {
        // 2, 1, 0 is a valid inverse CDF with ft = 4 (RFC 6716 §4.1.3.3).
        byte[] table = [2, 1, 0];
        var buffer = new byte[16];
        var encoder = new RangeEncoder(buffer);
        encoder.EncodeIcdf(table, 2, 1);
        encoder.Done();
        Assert.False(encoder.Error);

        var decoder = new RangeDecoder(buffer);
        Assert.Equal(1, decoder.DecodeIcdf(table, 2));
        Assert.False(decoder.Error);
    }
}

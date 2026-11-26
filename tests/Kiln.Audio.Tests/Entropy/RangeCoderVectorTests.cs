using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests;

/// <summary>
/// Hand-derived encoder vectors. Every byte below is worked out from the integer arithmetic of
/// RFC 6716 §5.1 in the comments; none was produced by running this or any other encoder.
/// </summary>
public class RangeCoderVectorTests
{
    [Fact]
    public void Single_symbol_emits_one_byte()
    {
        // RFC 6716 §5.1.1 and §5.1.5, worked by hand.
        // Initial state: (val, rng, rem, ext) = (0, 0x80000000, -1, 0).
        // Encode(fl = 2, fh = 3, ft = 4):
        //   r = rng / ft                = 0x20000000
        //   fl > 0, so val = 0 + 0x80000000 - 0x20000000 * (4 - 2)
        //                              = 0x80000000 - 0x40000000 = 0x40000000
        //               rng = 0x20000000 * (3 - 2) = 0x20000000
        //   rng > 2**23, so no renormalization.
        // Done (§5.1.5): the largest trailing-zero block that fits in
        // [0x40000000, 0x60000000) has b = 29, giving end = 0x40000000.
        //   end >> 23 = 0x80. CarryOut(0x80): carry = 0; rem was -1, so no byte is written;
        //     rem = 0x80.
        //   end' = (0x40000000 << 8) & 0x7FFFFFFF = 0, so the loop stops.
        //   rem != 0, so CarryOut(0) writes rem + 0 = 0x80.
        var buffer = new byte[4];
        var encoder = new RangeEncoder(buffer);

        encoder.Encode(2, 3, 4);
        encoder.Done();

        Assert.False(encoder.Error);
        Assert.Equal(1, encoder.RangeBytes);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x00, 0x00 }, buffer);
    }

    [Fact]
    public void Carry_propagation_writes_the_incremented_byte()
    {
        // RFC 6716 §5.1.1.2, worked by hand. Initial state (0, 0x80000000, -1, 0).
        // A: Encode(0, 1, 4):
        //      r = 0x20000000; fl = 0 => rng = 0x80000000 - 0x20000000 * (4 - 1) = 0x20000000.
        // B: Encode(3, 4, 8):
        //      r = 0x20000000 / 8 = 0x04000000
        //      val = 0 + 0x20000000 - 0x04000000 * (8 - 3) = 0x0C000000
        //      rng = 0x04000000 * 1 = 0x04000000
        // C: Encode(0, 7, 8):
        //      r = 0x04000000 / 8 = 0x00800000
        //      rng = 0x04000000 - 0x00800000 * (8 - 7) = 0x03800000
        // D: Encode(3, 4, 8):
        //      r = 0x03800000 / 8 = 0x00700000
        //      val = 0x0C000000 + 0x03800000 - 0x00700000 * (8 - 3) = 0x0D500000
        //      rng = 0x00700000
        //    rng = 0x700000 <= 2**23, so renormalize:
        //      c = val >> 23 = 0x1A = 26. CarryOut(26): carry = 0; rem was -1 (no byte);
        //        rem = 26.
        //      val = (0x0D500000 << 8) & 0x7FFFFFFF = 0x50000000
        //      rng = 0x70000000
        // Done: the fitting block has b = 30, end = 0x80000000.
        //   end >> 23 = 0x100 = 256. CarryOut(256): carry = 1; rem was 26, so the byte
        //     26 + 1 = 27 = 0x1B is written; rem = 0.
        //   end' = 0, so the loop stops. rem == 0 and ext == 0, so there is no final flush.
        var buffer = new byte[8];
        var encoder = new RangeEncoder(buffer);

        encoder.Encode(0, 1, 4);
        encoder.Encode(3, 4, 8);
        encoder.Encode(0, 7, 8);
        encoder.Encode(3, 4, 8);
        encoder.Done();

        Assert.False(encoder.Error);
        Assert.Equal(1, encoder.RangeBytes);
        Assert.Equal(new byte[] { 0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, buffer);
    }

    [Fact]
    public void Raw_bits_merge_at_end_of_frame()
    {
        // RFC 6716 §5.1.3 and §5.1.5, worked by hand.
        // Initial state (0, 0x80000000, -1, 0).
        // Encode(2, 3, 4): as in the first vector, val = 0x40000000, rng = 0x20000000.
        // EncodeBits(5, 3): end_window = 5, nend_bits = 3.
        // Done: end = 0x40000000. CarryOut(0x80) sets rem = 0x80; end' = 0.
        //   rem != 0, so CarryOut(0) writes 0x80 at the front.
        //   The three pending raw bits flush as 0x05 into the last byte of the buffer.
        var buffer = new byte[2];
        var encoder = new RangeEncoder(buffer);

        encoder.Encode(2, 3, 4);
        encoder.EncodeBits(5, 3);
        encoder.Done();

        Assert.False(encoder.Error);
        Assert.Equal(2, encoder.RangeBytes);
        Assert.Equal(new byte[] { 0x80, 0x05 }, buffer);
    }

    [Fact]
    public void Raw_bits_only_fill_the_last_byte()
    {
        // RFC 6716 §5.1.3 and §5.1.5, worked by hand.
        // EncodeBits(0x0B, 5): end_window = 0x0B, nend_bits = 5.
        // Done: val = 0 and rng = 0x80000000, so the fitting block has b = 31 and end = 0.
        //   rem is -1 and ext is zero, so no range bytes are emitted.
        //   The five pending raw bits flush as 0x0B into the last byte of the buffer.
        var buffer = new byte[3];
        var encoder = new RangeEncoder(buffer);

        encoder.EncodeBits(0x0B, 5);
        encoder.Done();

        Assert.False(encoder.Error);
        Assert.Equal(1, encoder.RangeBytes);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x0B }, buffer);
    }
}

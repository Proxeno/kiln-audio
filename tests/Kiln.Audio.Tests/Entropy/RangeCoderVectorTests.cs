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
        // Done (§5.1.5): rng = 0x20000000, so l = 32 - ilog(rng) = 2 and
        //   msk = (2**31 - 1) >> 2 = 0x1FFFFFFF.
        //   end = (0x40000000 + 0x1FFFFFFF) & ~0x1FFFFFFF = 0x40000000.
        //   (end | msk) = 0x5FFFFFFF < val + rng = 0x60000000, so the value stands.
        //   Emit while l > 0 (one byte): end >> 23 = 0x80. CarryOut(0x80): carry = 0;
        //     rem was -1, so no byte is written; rem = 0x80.
        //   end becomes (0x40000000 << 8) & 0x7FFFFFFF = 0; l = 2 - 8 = -6, loop ends.
        //   rem >= 0 (a byte is buffered), so CarryOut(0) writes rem + 0 = 0x80.
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
        //      val = (0x0D500000 << 8) & 0x7FFFFFFF = 0x55000000
        //      rng = 0x70000000
        // Done: l = 32 - ilog(0x70000000) = 1 and msk = 0x7FFFFFFF >> 1 = 0x3FFFFFFF.
        //   end = (0x55000000 + 0x3FFFFFFF) & ~0x3FFFFFFF = 0x80000000.
        //   (end | msk) = 0xBFFFFFFF < val + rng = 0xC5000000, so the value stands.
        //   Emit one byte: end >> 23 = 0x100 = 256. CarryOut(256): carry = 1; rem was
        //     26, so the byte 26 + 1 = 27 = 0x1B is written; the buffered byte becomes
        //     0x100 & 0xFF = 0; l = 1 - 8 = -7, loop ends.
        //   rem = 0 is still a buffered byte (rem >= 0), so the final CarryOut(0) writes
        //     it as 0x00. The frame is the two bytes 0x1B 0x00.
        var buffer = new byte[8];
        var encoder = new RangeEncoder(buffer);

        encoder.Encode(0, 1, 4);
        encoder.Encode(3, 4, 8);
        encoder.Encode(0, 7, 8);
        encoder.Encode(3, 4, 8);
        encoder.Done();

        Assert.False(encoder.Error);
        Assert.Equal(2, encoder.RangeBytes);
        Assert.Equal(new byte[] { 0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, buffer);
    }

    [Fact]
    public void Raw_bits_merge_at_end_of_frame()
    {
        // RFC 6716 §5.1.3 and §5.1.5, worked by hand.
        // Initial state (0, 0x80000000, -1, 0).
        // Encode(2, 3, 4): as in the first vector, val = 0x40000000, rng = 0x20000000.
        // EncodeBits(5, 3): end_window = 5, nend_bits = 3.
        // Done: as in the first vector, l = 2 and end = 0x40000000. CarryOut(0x80)
        //   buffers rem = 0x80, and CarryOut(0) writes 0x80 at the front; l = -6.
        //   The three pending raw bits (0x05) are ORed into the last byte of the frame.
        //   Here offs = 1 and end_offs = 0, so that byte is the zero-filled gap byte,
        //   not the range byte: the raw bits fit in a byte of their own.
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
        // Done: rng = 0x80000000, so l = 32 - ilog(rng) = 0 and end = 0; the emit
        //   loop does not run. rem is -1 and ext is zero, so no range bytes are emitted.
        //   The five pending raw bits (0x0B) are ORed into the last byte of the buffer.
        var buffer = new byte[3];
        var encoder = new RangeEncoder(buffer);

        encoder.EncodeBits(0x0B, 5);
        encoder.Done();

        Assert.False(encoder.Error);
        Assert.Equal(1, encoder.RangeBytes);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x0B }, buffer);
    }
}

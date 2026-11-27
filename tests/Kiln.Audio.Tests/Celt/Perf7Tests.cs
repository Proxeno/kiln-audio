using FluentAssertions;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests.Celt;

/// <summary>PERF-7 checks: the closed-form pair unranking and the threshold-count fractional tell.</summary>
public class Perf7Tests
{
    [Fact]
    public void Pvq_pair_decode_is_a_bijection_for_every_pulse_count()
    {
        for (int k = 1; k <= 128; k++)
        {
            uint size = CeltDecoder.PvqV(2, k);
            size.Should().Be((uint)(4 * k));
            var seen = new HashSet<(int, int)>();
            for (uint index = 0; index < size; index++)
            {
                int[] vector = DecodeVector(index, 2, k);
                (Math.Abs(vector[0]) + Math.Abs(vector[1])).Should().Be(k);
                seen.Add((vector[0], vector[1])).Should().BeTrue();
            }

            seen.Should().HaveCount((int)size);
        }
    }

    [Fact]
    public void Fractional_tell_matches_the_reference_loop_for_every_normalized_range()
    {
        var buffer = new byte[8];
        for (uint rQ15 = 32768; rQ15 < 65536; rQ15++)
        {
            // Build a range whose top 16 bits are rQ15 at two different magnitudes.
            foreach (int shift in new[] { 7, 15 })
            {
                var decoder = new RangeDecoder(buffer);
                decoder.SetRngForTest(rQ15 << shift);
                decoder.TellFrac().Should().Be(decoder.TellFracReference());
            }
        }
    }

    private static int[] DecodeVector(uint index, int n, int k)
    {
        Span<byte> buffer = stackalloc byte[32];
        var encoder = new RangeEncoder(buffer);
        encoder.EncodeUInt(index, CeltDecoder.PvqV(n, k));
        encoder.Done();
        var decoder = new RangeDecoder(buffer);
        var vector = new int[n];
        CeltDecoder.DecodePulses(ref decoder, n, k, vector);
        decoder.Error.Should().BeFalse();
        return vector;
    }
}

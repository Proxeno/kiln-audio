using FluentAssertions;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;

namespace Kiln.Audio.Tests.Celt;

/// <summary>
/// Hand-computed checks of the three smallest CELT primitives: the PVQ codebook size
/// V(N,K), the PVQ index decoder, and the Laplace energy decoder (RFC 6716 §4.3.2.1 and
/// §4.3.4.1).
/// </summary>
public class CeltPrimitiveTests
{
    [Theory]
    [InlineData(1, 1, 2u)]
    [InlineData(2, 1, 4u)]
    [InlineData(2, 2, 8u)]
    [InlineData(3, 1, 6u)]
    [InlineData(3, 2, 18u)]
    [InlineData(4, 2, 32u)]
    [InlineData(4, 3, 88u)]
    [InlineData(5, 1, 10u)]
    public void Pvq_codebook_size_matches_the_hand_counted_vectors(int n, int k, uint expected)
    {
        CeltDecoder.PvqV(n, k).Should().Be(expected);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(2, 3)]
    [InlineData(4, 2)]
    public void Pvq_index_decode_is_a_bijection_over_the_codebook(int n, int k)
    {
        uint size = CeltDecoder.PvqV(n, k);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (uint index = 0; index < size; index++)
        {
            int[] vector = DecodeVector(index, n, k);
            vector.Sum(value => Math.Abs(value)).Should().Be(k);
            seen.Add(string.Join(',', vector)).Should().BeTrue(
                "index {0} must map to a distinct vector of V({1},{2})", index, n, k);
        }

        seen.Should().HaveCount((int)size);
    }

    [Fact]
    public void Laplace_decoder_recovers_hand_chosen_symbols_from_a_hand_built_stream()
    {
        // The symbols below are chosen by hand; the intervals are computed from the PDF
        // description in RFC 6716 §4.3.2.1, independently of the decoder's search loop.
        const uint Fs0 = 10000;
        const int Decay = 10000;
        int[] symbols = [0, 1, -1, 2, -2, 0, 3, -4];

        Span<byte> buffer = stackalloc byte[64];
        var encoder = new RangeEncoder(buffer);
        foreach (int symbol in symbols)
        {
            (uint fl, uint fh) = LaplaceInterval(Fs0, Decay, symbol);
            encoder.EncodeBin(fl, fh, 15);
        }

        encoder.Done();
        encoder.Error.Should().BeFalse();

        var decoder = new RangeDecoder(buffer);
        var decoded = new int[symbols.Length];
        for (int i = 0; i < decoded.Length; i++)
        {
            decoded[i] = CeltDecoder.LaplaceDecode(ref decoder, Fs0, Decay);
        }

        decoded.Should().Equal(symbols);
        decoder.Error.Should().BeFalse();
    }

    private static int[] DecodeVector(uint index, int n, int k)
    {
        Span<byte> buffer = stackalloc byte[32];
        var encoder = new RangeEncoder(buffer);
        encoder.EncodeUInt(index, CeltDecoder.PvqV(n, k));
        encoder.Done();
        encoder.Error.Should().BeFalse();

        var decoder = new RangeDecoder(buffer);
        var vector = new int[n];
        float energy = CeltDecoder.DecodePulses(ref decoder, n, k, vector);
        energy.Should().BeApproximately(vector.Sum(value => value * value), 1e-3f);
        decoder.Error.Should().BeFalse();
        return vector;
    }

    /// <summary>
    /// Returns the cumulative interval the Laplace PDF assigns to <paramref name="value"/>
    /// (RFC 6716 §4.3.2.1), for building the test stream.
    /// </summary>
    private static (uint Fl, uint Fh) LaplaceInterval(uint fs0, int decay, int value)
    {
        if (value == 0)
        {
            return (0, fs0);
        }

        int magnitude = Math.Abs(value);
        uint fl = fs0;
        uint fs = LaplaceFrequency(fs0, decay) + 1;
        int level = 1;
        while (fs > 1 && level < magnitude)
        {
            fs *= 2;
            fl += fs;
            fs = (uint)((((int)fs - 2) * decay) >> 15) + 1;
            level++;
        }

        if (fs <= 1)
        {
            uint steps = (uint)(magnitude - level);
            fl += 2 * steps;
            fs = 1;
        }

        // The decoder negates the symbol when fm is in the first half [fl, fl+fs); the
        // second half [fl+fs, fl+2fs) is the positive value (RFC 6716 §4.3.2.1).
        return value > 0 ? (fl + fs, fl + (2 * fs)) : (fl, fl + fs);
    }

    private static uint LaplaceFrequency(uint fs0, int decay)
    {
        uint total = 32768 - 32 - fs0;
        return (uint)(((ulong)total * (uint)(16384 - decay)) >> 15);
    }
}

using FluentAssertions;
using Kiln.Audio.Celt.Encoder;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>Each vector kernel must agree with its scalar reference.</summary>
public class EncoderKernelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(120)]
    [InlineData(960)]
    public void NormaliseBand_vector_matches_scalar_exactly(int length)
    {
        var random = new Random(length);
        var input = new float[length];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)(random.NextDouble() * 2 - 1);
        }

        const float amplitude = 3.25f;
        float gain = 1f / (1e-27f + amplitude);

        var vector = input.AsSpan().ToArray();
        var scalar = input.AsSpan().ToArray();
        CeltEncoderKernels.NormaliseBand(vector, amplitude);
        CeltEncoderKernels.NormaliseBandScalar(scalar, gain);

        vector.AsSpan().SequenceEqual(scalar).Should().BeTrue(
            "the vector path must produce bit-identical coefficients");
    }

    [Fact]
    public void BandEnergy_matches_its_scalar_reference()
    {
        var random = new Random(42);
        var input = new float[960];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)(random.NextDouble() * 2 - 1);
        }

        CeltEncoderKernels.BandEnergy(input).Should().Be(CeltEncoderKernels.BandEnergyScalar(input));
    }
}

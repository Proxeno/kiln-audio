using FluentAssertions;
using Kiln.Audio.Celt.Encoder;
using Kiln.Audio.Simd;

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
    public void NormaliseInto_vector_matches_the_scalar_reference(int length)
    {
        var random = new Random(length + 3);
        var input = new float[length];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)((random.NextDouble() * 2) - 1);
        }

        const float gain = 0.375f;
        var vectorOutput = new float[length];
        var scalarOutput = new float[length];
        EncKernels.NormaliseInto(input, vectorOutput, gain, useVector: true);
        EncKernels.NormaliseInto(input, scalarOutput, gain, useVector: false);

        vectorOutput.Should().Equal(scalarOutput);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(960)]
    public void Scale_vector_matches_the_scalar_reference(int length)
    {
        var random = new Random(length + 11);
        var input = new float[length];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)((random.NextDouble() * 2) - 1);
        }

        const float scale = 0.0020833334f;
        var vectorOutput = input.AsSpan().ToArray();
        var scalarOutput = input.AsSpan().ToArray();
        EncKernels.Scale(vectorOutput, scale, useVector: true);
        EncKernels.Scale(scalarOutput, scale, useVector: false);

        vectorOutput.Should().Equal(scalarOutput);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(120)]
    [InlineData(960)]
    public void Pre_emphasis_vector_matches_the_scalar_reference(int length)
    {
        var random = new Random(length + 19);
        var pcm = new short[length];
        for (int i = 0; i < pcm.Length; i++)
        {
            pcm[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }

        var vectorOutput = new float[length];
        var scalarOutput = new float[length];
        float vectorMemory = 0.125f;
        float scalarMemory = 0.125f;

        EncKernels.PreEmphasis(pcm, vectorOutput, ref vectorMemory, out bool vectorSilent);
        CeltEncoderKernels.PreEmphasisScalar(pcm, scalarOutput, ref scalarMemory, out bool scalarSilent);

        vectorOutput.Should().Equal(scalarOutput);
        vectorMemory.Should().Be(scalarMemory);
        vectorSilent.Should().Be(scalarSilent);
    }

    [Fact]
    public void Pre_emphasis_reports_silence()
    {
        var pcm = new short[120];
        var output = new float[pcm.Length];
        float memory = 0f;
        EncKernels.PreEmphasis(pcm, output, ref memory, out bool silent);
        silent.Should().BeTrue();
        output.Should().AllSatisfy(static value => value.Should().Be(0f));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 4)]
    [InlineData(8, 3)]
    [InlineData(8, 12)]
    [InlineData(16, 5)]
    [InlineData(24, 18)]
    [InlineData(48, 10)]
    [InlineData(96, 32)]
    [InlineData(144, 40)]
    [InlineData(176, 60)]
    public void PvqSearch_vector_matches_the_scalar_reference(int n, int k)
    {
        for (int seed = 0; seed < 4; seed++)
        {
            var random = new Random((seed * 977) + n + k);
            var input = new float[n];
            for (int i = 0; i < n; i++)
            {
                input[i] = (float)((random.NextDouble() * 2) - 1);
            }

            var vectorX = input.AsSpan().ToArray();
            var scalarX = input.AsSpan().ToArray();
            var vectorIy = new int[n];
            var scalarIy = new int[n];
            var vectorSigns = new float[n];
            var scalarSigns = new float[n];
            var vectorY = new float[n];
            var scalarY = new float[n];

            float vectorNorm = EncPvqKernel.Search(vectorX, vectorIy, vectorSigns, vectorY, n, k);
            float scalarNorm = CeltEncoderKernels.PvqSearchScalar(scalarX, scalarIy, scalarSigns, scalarY, n, k);

            scalarNorm.Should().Be(vectorNorm);
            scalarIy.Should().Equal(vectorIy);
            scalarSigns.Should().Equal(vectorSigns);
            scalarX.Should().Equal(vectorX);
            scalarY.Should().Equal(vectorY);
        }
    }
}

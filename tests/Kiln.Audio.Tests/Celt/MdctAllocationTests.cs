using Kiln.Audio.Celt;

namespace Kiln.Audio.Tests;

/// <summary>The FFT and MDCT execute paths must not allocate managed memory.</summary>
public class MdctAllocationTests
{
    [Fact]
    public void Fft_execute_allocates_nothing()
    {
        const int Size = 480;
        var fft = Fft.Get(Size);
        var input = new Complex32[Size];
        var output = new Complex32[Size];
        var random = new Random(3);
        for (int i = 0; i < Size; i++)
        {
            input[i] = new Complex32(
                (float)((2.0 * random.NextDouble()) - 1.0),
                (float)((2.0 * random.NextDouble()) - 1.0));
        }

        for (int warmup = 0; warmup < 5; warmup++)
        {
            fft.Forward(input, output);
            fft.Inverse(input, output);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        fft.Forward(input, output);
        fft.Inverse(input, output);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }

    [Fact]
    public void Mdct_execute_allocates_nothing()
    {
        const int CoefficientCount = 960;
        var mdct = new Mdct(CoefficientCount);
        var input = new float[CoefficientCount + CeltWindow.DefaultOverlap];
        var coefficients = new float[CoefficientCount];
        var output = new float[CoefficientCount + CeltWindow.DefaultOverlap];
        var random = new Random(5);
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)((2.0 * random.NextDouble()) - 1.0);
        }

        for (int warmup = 0; warmup < 5; warmup++)
        {
            mdct.Forward(input, coefficients);
            mdct.Inverse(coefficients, output);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        mdct.Forward(input, coefficients);
        mdct.Inverse(coefficients, output);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(before, after);
    }
}

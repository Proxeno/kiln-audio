using BenchmarkDotNet.Attributes;
using Kiln.Audio.Celt;

namespace Kiln.Audio.Benchmarks;

/// <summary>Scalar inverse MDCT for the 20 ms long block and the 8 x 2.5 ms transient case.</summary>
[MemoryDiagnoser]
public class MdctBenchmarks
{
    private const int Overlap = 120;
    private const int ShortBlocks = 8;
    private const int ShortCount = 120;
    private const int LongCount = 960;

    private readonly Mdct _long = new(LongCount, Overlap);
    private readonly Mdct _short = new(ShortCount, Overlap);
    private readonly float[] _longCoefficients = new float[LongCount];
    private readonly float[] _longOutput = new float[LongCount + Overlap];
    private readonly float[] _shortCoefficients = new float[ShortBlocks * ShortCount];
    private readonly float[] _shortOutput = new float[ShortBlocks * (ShortCount + Overlap)];

    /// <summary>Fills the coefficient buffers with a deterministic non-trivial pattern.</summary>
    public MdctBenchmarks()
    {
        for (int i = 0; i < _longCoefficients.Length; i++)
        {
            _longCoefficients[i] = (float)Math.Sin(0.37 * i);
        }

        for (int i = 0; i < _shortCoefficients.Length; i++)
        {
            _shortCoefficients[i] = (float)Math.Cos(0.53 * i);
        }
    }

    /// <summary>One inverse MDCT of a 20 ms (960-coefficient) block.</summary>
    [Benchmark]
    public void Inverse960() => _long.Inverse(_longCoefficients, _longOutput);

    /// <summary>Eight interleaved 2.5 ms (120-coefficient) inverse MDCTs.</summary>
    [Benchmark]
    public void Inverse8x120()
    {
        for (int block = 0; block < ShortBlocks; block++)
        {
            _short.Inverse(
                _shortCoefficients.AsSpan(block),
                _shortOutput.AsSpan(block * (ShortCount + Overlap)),
                stride: ShortBlocks);
        }
    }
}

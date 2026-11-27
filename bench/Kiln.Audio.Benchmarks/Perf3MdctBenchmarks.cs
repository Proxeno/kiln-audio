using BenchmarkDotNet.Attributes;
using Kiln.Audio.Celt;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// PERF-3 MDCT timings: one complex FFT per direction instead of the two DCT-IV FFTs of PERF-1.
/// </summary>
/// <remarks>
/// Inputs are the real transform sizes the CELT decoder runs at 48 kHz: the 20 ms 960-coefficient
/// long block and the 2.5 ms 120-coefficient short block. The 8 x 120 benchmark is the transient
/// frame, one interleaved short-block MDCT per block.
/// </remarks>
[MemoryDiagnoser]
public class Perf3MdctBenchmarks
{
    private const int Overlap = 120;
    private const int ShortBlocks = 8;

    private readonly Mdct _long = new(960, Overlap);
    private readonly Mdct _short = new(120, Overlap);
    private readonly float[] _longInput = new float[960 + Overlap];
    private readonly float[] _longCoefficients = new float[960];
    private readonly float[] _longOutput = new float[960 + Overlap];
    private readonly float[] _shortInput = new float[ShortBlocks * (120 + Overlap)];
    private readonly float[] _shortCoefficients = new float[ShortBlocks * 120];
    private readonly float[] _shortOutput = new float[120 + Overlap];

    /// <summary>Fills the input with a deterministic broadband signal.</summary>
    public Perf3MdctBenchmarks()
    {
        for (int i = 0; i < _longInput.Length; i++)
        {
            _longInput[i] = (float)Math.Sin(0.017 * i) + (0.3f * (float)Math.Cos(0.11 * i));
        }

        for (int i = 0; i < _shortInput.Length; i++)
        {
            _shortInput[i] = (float)Math.Sin(0.29 * i) + (0.2f * (float)Math.Cos(0.71 * i));
        }
    }

    /// <summary>One 960-coefficient forward MDCT.</summary>
    [Benchmark]
    public void Forward960() => _long.Forward(_longInput, _longCoefficients);

    /// <summary>One 960-coefficient inverse MDCT.</summary>
    [Benchmark]
    public void Inverse960() => _long.Inverse(_longCoefficients, _longOutput);

    /// <summary>One 120-coefficient forward MDCT.</summary>
    [Benchmark]
    public void Forward120() =>
        _short.Forward(_shortInput.AsSpan(0, 120 + Overlap), _shortCoefficients.AsSpan(0, 120));

    /// <summary>One 120-coefficient inverse MDCT.</summary>
    [Benchmark]
    public void Inverse120() =>
        _short.Inverse(_shortCoefficients.AsSpan(0, 120), _shortOutput);

    /// <summary>Eight interleaved 120-coefficient forward MDCTs (one transient frame).</summary>
    [Benchmark]
    public void Forward8x120()
    {
        for (int block = 0; block < ShortBlocks; block++)
        {
            _short.Forward(
                _shortInput.AsSpan(block * (120 + Overlap), 120 + Overlap),
                _shortCoefficients.AsSpan(block),
                stride: ShortBlocks);
        }
    }

    /// <summary>Eight interleaved 120-coefficient inverse MDCTs (one transient frame).</summary>
    [Benchmark]
    public void Inverse8x120()
    {
        for (int block = 0; block < ShortBlocks; block++)
        {
            _short.Inverse(
                _shortCoefficients.AsSpan(block),
                _shortOutput,
                stride: ShortBlocks);
        }
    }
}

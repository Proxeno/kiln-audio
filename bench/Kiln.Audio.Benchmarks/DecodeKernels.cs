using BenchmarkDotNet.Attributes;
using Kiln.Audio.Celt;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// Side-by-side scalar and vector timing of the decoder kernels added for PERF-2.
/// </summary>
/// <remarks>
/// Each pair runs the same work with the vector path forced on and off, so the ratio within a
/// row is a same-process comparison and is far less sensitive to the shared machine's load than
/// comparing two separate benchmark runs.
/// </remarks>
[MemoryDiagnoser]
public class DecodeKernelBenchmarks
{
    private const int LtpSubframeLength = 80;
    private const int LtpReadIndex = 400;
    private const int LtpLag = 32;
    private const int LtpBufferLength = 2 * 320;

    private int[] _ltpInitial = [];
    private int[] _ltpBuffer = [];
    private short[] _ltpCoefficients = [];
    private int[] _excitation = [];
    private int[] _residual = [];

    private float[] _spectrum = [];
    private float[] _destination = [];
    private int[] _pulses = [];

    /// <summary>Builds the deterministic kernel inputs.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(12345);
        _ltpInitial = new int[LtpBufferLength];
        for (int i = 0; i < _ltpInitial.Length; i++)
        {
            _ltpInitial[i] = random.Next(-1 << 20, 1 << 20);
        }

        _ltpBuffer = new int[LtpBufferLength];
        _ltpInitial.CopyTo(_ltpBuffer, 0);
        _ltpCoefficients = new short[DecSilkLtp.Taps];
        for (int i = 0; i < _ltpCoefficients.Length; i++)
        {
            _ltpCoefficients[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
        }

        _excitation = new int[LtpSubframeLength];
        _residual = new int[LtpSubframeLength];
        for (int i = 0; i < LtpSubframeLength; i++)
        {
            _excitation[i] = random.Next(-1 << 24, 1 << 24);
        }

        _spectrum = new float[CeltTables.MaxFrameSamples];
        _destination = new float[CeltTables.MaxFrameSamples];
        _pulses = new int[CeltTables.MaxFrameSamples];
        for (int i = 0; i < _spectrum.Length; i++)
        {
            _spectrum[i] = (float)((2.0 * random.NextDouble()) - 1.0);
            _pulses[i] = random.Next(-6, 7);
        }
    }

    /// <summary>The SILK LTP loop, scalar path.</summary>
    [Benchmark]
    public void SilkLtpScalar() =>
        DecSilkLtp.Synthesize(
            _ltpBuffer, LtpReadIndex, LtpReadIndex + LtpLag - 2, _ltpCoefficients, _excitation, _residual,
            LtpSubframeLength, useVector: false);

    /// <summary>The SILK LTP loop, vector path.</summary>
    [Benchmark]
    public void SilkLtpVector() =>
        DecSilkLtp.Synthesize(
            _ltpBuffer, LtpReadIndex, LtpReadIndex + LtpLag - 2, _ltpCoefficients, _excitation, _residual,
            LtpSubframeLength, useVector: true);

    /// <summary>Band denormalization of a 176-coefficient span, scalar path.</summary>
    [Benchmark]
    public void DenormaliseScalar() =>
        DecKernels.ScaleCopy(_destination.AsSpan(0, 176), _spectrum.AsSpan(0, 176), 0.5f, useVector: false);

    /// <summary>Band denormalization of a 176-coefficient span, vector path.</summary>
    [Benchmark]
    public void DenormaliseVector() =>
        DecKernels.ScaleCopy(_destination.AsSpan(0, 176), _spectrum.AsSpan(0, 176), 0.5f, useVector: true);

    /// <summary>PVQ pulse-vector scaling of a 176-coefficient span, scalar path.</summary>
    [Benchmark]
    public void PulseScaleScalar() =>
        DecKernels.ScaleIntToFloat(_destination.AsSpan(0, 176), _pulses.AsSpan(0, 176), 0.0137f, useVector: false);

    /// <summary>PVQ pulse-vector scaling of a 176-coefficient span, vector path.</summary>
    [Benchmark]
    public void PulseScaleVector() =>
        DecKernels.ScaleIntToFloat(_destination.AsSpan(0, 176), _pulses.AsSpan(0, 176), 0.0137f, useVector: true);
}
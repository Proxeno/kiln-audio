using BenchmarkDotNet.Attributes;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Simd;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// Microbenchmarks for the individual CELT and SILK kernels that dominate decode time.
/// </summary>
/// <remarks>
/// The SILK LPC and LTP methods mirror the exact inner loops of
/// <c>SilkSynthesis.SynthesizeChannel</c> and call the same internal
/// <see cref="SilkFixedPoint"/> helpers. That method is private and this lane is
/// measurement-only, so the loops cannot be reached directly; the arithmetic, array layout
/// and fixed-point operations are copied verbatim from the source. The CELT kernels call the
/// production methods through <c>InternalsVisibleTo</c>. The mutable scratch buffers are reused
/// across invocations exactly as the codec reuses them; none of the kernels has a branch that
/// depends on the data, so this does not change the measured work.
/// </remarks>
[MemoryDiagnoser]
public class KernelBenchmarks
{
    private const int Overlap = 120;
    private const int ShortBlocks = 8;
    private const int ShortCount = 120;
    private const int LongCount = 960;
    private const int HistorySize = 2048;

    private const int PvqBandSamples = 96;
    private const int PvqPulses = 32;

    private const int LpcOrder = 16;
    private const int SubframeLength = 80;
    private const int Subframes = 4;
    private const int LtpOrder = 5;
    private const int LtpMemory = 320;
    private const int LpcStateLength = LpcOrder + SubframeLength;

    private readonly Mdct _longMdct = new(LongCount, Overlap);
    private readonly Mdct _shortMdct = new(ShortCount, Overlap);
    private readonly float[] _longCoefficients = new float[LongCount];
    private readonly float[] _longOutput = new float[LongCount + Overlap];
    private readonly float[] _longInput = new float[LongCount + Overlap];
    private readonly float[] _shortCoefficients = new float[ShortBlocks * ShortCount];
    private readonly float[] _shortOutput = new float[ShortBlocks * (ShortCount + Overlap)];
    private readonly float[] _shortInput = new float[ShortBlocks * (ShortCount + Overlap)];

    private readonly byte[] _pvqBytes = new byte[128];
    private readonly int[] _pvq = new int[PvqBandSamples];

    private readonly CeltSynthesizer _postFilter = new(2);
    private readonly float[] _history = new float[HistorySize];

    private readonly int[] _lpcState = new int[LpcStateLength];
    private readonly short[] _lpcCoefficients = new short[LpcOrder];
    private readonly int[] _residual = new int[SubframeLength];
    private readonly short[] _lpcOutput = new short[Subframes * SubframeLength];

    private readonly int[] _ltpState = new int[2 * LtpMemory];
    private readonly short[] _ltpCoefficients = new short[Subframes * LtpOrder];
    private readonly int[] _excitation = new int[SubframeLength];

    private readonly Fft _fft480 = Fft.Get(480);
    private readonly Complex32[] _fftInput = new Complex32[480];
    private readonly Complex32[] _fftOutput = new Complex32[480];

    private int _gainQ10 = 4096;

    /// <summary>Fills every buffer with a deterministic, non-trivial pattern.</summary>
    public KernelBenchmarks()
    {
        for (int i = 0; i < _longCoefficients.Length; i++)
        {
            _longCoefficients[i] = (float)Math.Sin(0.37 * i);
        }

        for (int i = 0; i < _shortCoefficients.Length; i++)
        {
            _shortCoefficients[i] = (float)Math.Cos(0.53 * i);
        }

        for (int i = 0; i < _longInput.Length; i++)
        {
            _longInput[i] = (float)Math.Sin(0.29 * i);
        }

        for (int i = 0; i < _shortInput.Length; i++)
        {
            _shortInput[i] = (float)Math.Cos(0.61 * i);
        }

        for (int i = 0; i < _pvqBytes.Length; i++)
        {
            _pvqBytes[i] = (byte)((i * 37) + 11);
        }

        for (int i = 0; i < HistorySize; i++)
        {
            _history[i] = 0.25f * (float)Math.Sin(0.11 * i);
        }

        for (int i = 0; i < LpcStateLength; i++)
        {
            _lpcState[i] = ((i % 7) - 3) * 1024;
        }

        for (int i = 0; i < LpcOrder; i++)
        {
            _lpcCoefficients[i] = (short)(((i % 5) * 512) - 1024);
        }

        for (int i = 0; i < SubframeLength; i++)
        {
            _residual[i] = ((i * 37) % 200) - 100;
            _excitation[i] = ((i * 53) % 160) - 80;
        }

        for (int i = 0; i < _ltpState.Length; i++)
        {
            _ltpState[i] = ((i * 13) % 2000) - 1000;
        }

        for (int i = 0; i < _fftInput.Length; i++)
        {
            _fftInput[i] = new Complex32(
                (float)Math.Sin(0.23 * i), (float)Math.Cos(0.41 * i));
        }

        for (int i = 0; i < _ltpCoefficients.Length; i++)
        {
            _ltpCoefficients[i] = (short)(((i % 7) - 3) * 900);
        }
    }

    /// <summary>One 480-point complex forward FFT, the size one 960-coefficient MDCT uses per DCT-IV.</summary>
    [Benchmark]
    public void Fft480() => _fft480.Forward(_fftInput, _fftOutput);

    /// <summary>One forward MDCT of a 20 ms (960-coefficient) block.</summary>
    [Benchmark]
    public void MdctForward960() => _longMdct.Forward(_longInput, _longCoefficients, 1);

    /// <summary>Eight interleaved 2.5 ms (120-coefficient) forward MDCTs.</summary>
    [Benchmark]
    public void MdctForward8x120()
    {
        for (int block = 0; block < ShortBlocks; block++)
        {
            _shortMdct.Forward(
                _shortInput.AsSpan(block * (ShortCount + Overlap)),
                _shortCoefficients.AsSpan(block),
                stride: ShortBlocks);
        }
    }

    /// <summary>One inverse MDCT of a 20 ms (960-coefficient) block.</summary>
    [Benchmark]
    public void MdctInverse960() => _longMdct.Inverse(_longCoefficients, _longOutput);

    /// <summary>Eight interleaved 2.5 ms (120-coefficient) inverse MDCTs.</summary>
    [Benchmark]
    public void MdctInverse8x120()
    {
        for (int block = 0; block < ShortBlocks; block++)
        {
            _shortMdct.Inverse(
                _shortCoefficients.AsSpan(block),
                _shortOutput.AsSpan(block * (ShortCount + Overlap)),
                stride: ShortBlocks);
        }
    }

    /// <summary>One PVQ band decode: 96 coefficients (band 18 at 20 ms) carrying 32 pulses.</summary>
    [Benchmark]
    public int PvqDecodeBand()
    {
        var decoder = new RangeDecoder(_pvqBytes);
        CeltDecoder.DecodePulses(ref decoder, PvqBandSamples, PvqPulses, _pvq);
        return _pvq[0] + _pvq[PvqBandSamples - 1];
    }

    /// <summary>The CELT post-filter over a 960-sample frame with a cross-faded tap set.</summary>
    [Benchmark]
    public void CeltPostFilter()
    {
        _postFilter.CombFilter(
            _history,
            HistorySize - LongCount,
            LongCount,
            period0: 256,
            period1: 320,
            gain0: 0.5f,
            gain1: 0.5f,
            tapSet0: 0,
            tapSet1: 1);
    }

    /// <summary>Short-term LPC synthesis of one 20 ms, 16 kHz SILK frame (order 16).</summary>
    [Benchmark]
    public int SilkLpcSynthesis()
    {
        int offset = 0;
        for (int k = 0; k < Subframes; k++)
        {
            SilkLpcKernel.Synthesize(
                _lpcState,
                _lpcCoefficients,
                _residual,
                _gainQ10,
                LpcOrder,
                SubframeLength,
                _lpcOutput.AsSpan(offset));

            Array.Copy(_lpcState, SubframeLength, _lpcState, 0, LpcOrder);
            offset += SubframeLength;
        }

        return _lpcOutput[^1];
    }

    /// <summary>Long-term (pitch) prediction of one 20 ms, 16 kHz SILK frame with five taps.</summary>
    [Benchmark]
    public int SilkLtpSynthesis()
    {
        const int lag = 200;
        int bufferIndex = LtpMemory;
        for (int k = 0; k < Subframes; k++)
        {
            int predictionLag = bufferIndex - lag + (LtpOrder / 2);
            int coefficient = k * LtpOrder;
            for (int i = 0; i < SubframeLength; i++)
            {
                int predictionQ13 = 2;
                predictionQ13 = SilkFixedPoint.Smlawb(
                    predictionQ13, _ltpState[predictionLag], _ltpCoefficients[coefficient]);
                predictionQ13 = SilkFixedPoint.Smlawb(
                    predictionQ13, _ltpState[predictionLag - 1], _ltpCoefficients[coefficient + 1]);
                predictionQ13 = SilkFixedPoint.Smlawb(
                    predictionQ13, _ltpState[predictionLag - 2], _ltpCoefficients[coefficient + 2]);
                predictionQ13 = SilkFixedPoint.Smlawb(
                    predictionQ13, _ltpState[predictionLag - 3], _ltpCoefficients[coefficient + 3]);
                predictionQ13 = SilkFixedPoint.Smlawb(
                    predictionQ13, _ltpState[predictionLag - 4], _ltpCoefficients[coefficient + 4]);
                predictionLag++;

                _residual[i] = SilkFixedPoint.Add(
                    _excitation[i], SilkFixedPoint.Lshift(predictionQ13, 1));
                _ltpState[bufferIndex] = SilkFixedPoint.Lshift(_residual[i], 1);
                bufferIndex++;
            }
        }

        return _residual[SubframeLength - 1];
    }
}

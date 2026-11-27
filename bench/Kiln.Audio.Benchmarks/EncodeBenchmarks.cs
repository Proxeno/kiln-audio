using BenchmarkDotNet.Attributes;
using Kiln.Audio;
using Kiln.Audio.Celt;
using Kiln.Audio.Celt.Encoder;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// Encodes the whole RFC 8251 corpus (mono downmix) from memory at 20 ms, 24 and 32 kbps CBR,
/// for complexity 0, 5 and 10.
/// </summary>
/// <remarks>
/// The corpus is loaded in <see cref="GlobalSetup"/>, so no file IO is measured. The corpus
/// duration is reported in the summary; divide by the mean time for the real-time factor and by
/// the frame count for the per-frame cost.
/// </remarks>
[MemoryDiagnoser]
[MinIterationCount(2)]
[MaxIterationCount(3)]
[WarmupCount(1)]
public class EncodeBenchmarks
{
    private const int FrameSamples = 960;

    private short[] _corpus = [];
    private byte[] _packet = [];
    private OpusEncoder _encoder = null!;

    /// <summary>The encoder complexity under test.</summary>
    [Params(0, 5, 10)]
    public int Complexity { get; set; }

    /// <summary>The CBR target bitrate under test.</summary>
    [Params(24000, 32000)]
    public int Bitrate { get; set; }

    /// <summary>The total audio duration of the loaded corpus, in seconds.</summary>
    public double CorpusSeconds => _corpus.Length / 48000.0;

    /// <summary>The number of 20 ms frames in the corpus.</summary>
    public int FrameCount => _corpus.Length / FrameSamples;

    [GlobalSetup]
    public void Setup()
    {
        _corpus = CorpusData.LoadMono();
        _packet = new byte[1276];
        _encoder = new OpusEncoder(new OpusEncoderOptions
        {
            Application = OpusApplication.RestrictedLowDelay,
            BitrateBps = Bitrate,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = Complexity,
        });
    }

    [GlobalCleanup]
    public void Cleanup() => _encoder.Dispose();

    [Benchmark]
    public long EncodeCorpus()
    {
        long total = 0;
        for (int offset = 0; offset + FrameSamples <= _corpus.Length; offset += FrameSamples)
        {
            total += _encoder.Encode(_corpus.AsSpan(offset, FrameSamples), _packet);
        }

        return total;
    }
}

/// <summary>Microbenchmarks for the hottest encoder kernels.</summary>
[MemoryDiagnoser]
[BenchmarkCategory("EncodeKernel")]
[MinIterationCount(4)]
[MaxIterationCount(5)]
public class EncodeKernelBenchmarks
{
    private readonly short[] _pcm = new short[960];
    private readonly float[] _destination = new float[960];
    private readonly float[] _band = new float[960];
    private readonly float[] _transientInput = new float[1080];
    private readonly float[] _transientScratch = new float[1080];
    private readonly float[] _transientBins = new float[18];
    private readonly int[] _iy = new int[96];
    private readonly float[] _signs = new float[96];
    private readonly float[] _y = new float[96];
    private readonly Mdct _mdct960 = new(960, 120);
    private readonly Mdct _mdct120 = new(120, 120);
    private readonly float[] _mdctInput = new float[1080];
    private readonly float[] _mdctCoefficients = new float[960];
    private readonly float[] _shortInput = new float[1080];
    private readonly float[] _shortCoefficients = new float[960];
    private readonly float[] _preEmphasisMemory = new float[1];

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1);
        for (int i = 0; i < _pcm.Length; i++)
        {
            _pcm[i] = (short)random.Next(-20000, 20000);
            _band[i] = (float)(random.NextDouble() * 2 - 1);
            _transientInput[i] = (float)(random.NextDouble() * 20000 - 10000);
        }

        for (int i = 960; i < _transientInput.Length; i++)
        {
            _transientInput[i] = (float)(random.NextDouble() * 20000 - 10000);
            _mdctInput[i] = _transientInput[i];
            _shortInput[i] = _transientInput[i];
        }

        for (int i = 0; i < 960; i++)
        {
            _mdctInput[i] = _transientInput[i];
            _shortInput[i] = _transientInput[i];
        }
    }

    [Benchmark]
    [BenchmarkCategory("EncodeKernel")]
    public void MdctForward960() => _mdct960.Forward(_mdctInput, _mdctCoefficients);

    [Benchmark]
    [BenchmarkCategory("EncodeKernel")]
    public void MdctForward8x120()
    {
        for (int b = 0; b < 8; b++)
        {
            _mdct120.Forward(_shortInput.AsSpan(b * 120, 240), _shortCoefficients.AsSpan(b), 8);
        }
    }

    [Benchmark]
    [BenchmarkCategory("EncodeKernel")]
    public void PreEmphasis()
    {
        CeltEncoderKernels.PreEmphasis(
            _pcm, channels: 1, upsample: 1, frameSamples: 960, stride: 960, offset: 0,
            destination: _destination, memory: _preEmphasisMemory, silent: out _);
    }

    [Benchmark]
    [BenchmarkCategory("EncodeKernel")]
    public float BandEnergy() => CeltEncoderKernels.BandEnergy(_band);

    [Benchmark]
    [BenchmarkCategory("EncodeKernel")]
    public void NormaliseBand() => CeltEncoderKernels.NormaliseBand(_band, 1f);

    [Benchmark]
    [BenchmarkCategory("EncodeKernel")]
    public void TransientAnalysis() =>
        CeltEncoderKernels.AnalyzeTransient<Kiln.Audio.Simd.Kernels.Vector128Kernels>(
            _transientInput, channels: 1, frameSamples: 960, overlap: 120, tmp: _transientScratch,
            bins: _transientBins);

    [Benchmark]
    [BenchmarkCategory("EncodeKernel")]
    public float PvqSearch() => CeltEncoderKernels.PvqSearch(_band.AsSpan(0, 96), _iy, _signs, _y, 96, 32);
}

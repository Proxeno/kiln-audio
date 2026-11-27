using BenchmarkDotNet.Attributes;
using Kiln.Audio;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// Encodes the whole RFC 8251 corpus (mono downmix) from memory with the SILK encoder at
/// wideband, 20 ms frames and 20 kbps, for every complexity level.
/// </summary>
/// <remarks>
/// <para>
/// This is the SILK counterpart of <see cref="EncodeBenchmarks"/>. The corpus is loaded in
/// <see cref="GlobalSetup"/>, so no file IO is measured; divide the mean time by
/// <see cref="CorpusSeconds"/> for the real-time factor and by <see cref="FrameCount"/> for the
/// per-frame cost.
/// </para>
/// <para>
/// Compare against libopus with <c>scripts/bench-libopus-inproc.sh encode &lt;pcm&gt; 48000 1 voip
/// 20000 20 &lt;complexity&gt; 0</c> at the same complexity. Complexity selects the analysis
/// effort: 0-1 use the minimum-complexity pitch search, an order-8 shaping filter and the low
/// complexity LTP quantizer with 2 NLSF survivors; 2-3 raise the pitch LPC to order 8 and the
/// shaping filter to order 10 with 4 survivors; 4-5 use order 10/12, interpolation, the full LTP
/// quantizer and 8 survivors; 6-7 use 12/14 and 16 survivors; 8-10 use the maximum-complexity
/// pitch search, 16/16 and 32 survivors. Warping is enabled from complexity 4 up.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[MinIterationCount(2)]
[MaxIterationCount(3)]
[WarmupCount(1)]
public class SilkEncodeBenchmarks
{
    private const int FrameSamples = 960;

    private short[] _corpus = [];
    private byte[] _packet = [];
    private OpusEncoder _encoder = null!;

    /// <summary>The SILK encoder complexity under test.</summary>
    [Params(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)]
    public int Complexity { get; set; }

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
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 1,
            Bandwidth = OpusBandwidth.Wideband,
            BitrateBps = 20000,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = Complexity,
        });
    }

    [GlobalCleanup]
    public void Cleanup() => _encoder.Dispose();

    /// <summary>Encodes the corpus and returns the total number of payload bytes.</summary>
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

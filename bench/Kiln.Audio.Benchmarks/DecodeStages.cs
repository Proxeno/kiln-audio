using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// Breaks CELT decode down by stage: bitstream + frequency-domain decode, synthesis
/// (denormalization, inverse MDCT, post-filter, de-emphasis) and the int16 conversion.
/// </summary>
/// <remarks>
/// The stage timers use <see cref="Stopwatch.GetTimestamp"/> around each call and accumulate
/// over every invocation, so the report is a per-frame average in microseconds. Two stopwatch
/// reads per frame add well under 0.1 µs, which is below the resolution this breakdown needs.
/// </remarks>
[MemoryDiagnoser]
public class CeltDecodeStageBenchmarks
{
    private const int OutputChannels = 2;

    private byte[][] _packets = [];
    private Range[] _frameRanges = [];
    private CeltDecoder _decoder = null!;
    private CeltSynthesizer _synthesizer = null!;
    private CeltFrameResult _frame = null!;
    private float[] _floatPcm = [];
    private short[] _pcm = [];

    private long _decodeTicks;
    private long _synthesisTicks;
    private long _convertTicks;
    private long _frameCount;

    /// <summary>Gets or sets the RFC 8251 vector number to decode.</summary>
    [Params(1, 7, 11)]
    public int Vector { get; set; }

    /// <summary>Reads the vector into memory and constructs the reusable decoder state.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _packets = TestVectorData.ReadPackets(Vector);
        _frameRanges = new Range[OpusPacket.MaxFrames];
        _decoder = new CeltDecoder(OutputChannels);
        _synthesizer = new CeltSynthesizer(OutputChannels);
        _frame = new CeltFrameResult();
        _floatPcm = new float[OutputChannels * CeltTables.MaxFrameSamples];
        _pcm = new short[OutputChannels * CeltTables.MaxFrameSamples];
        _decoder.Reset();
        _synthesizer.Reset();
    }

    /// <summary>Decodes the whole vector, accumulating the per-stage cost.</summary>
    /// <returns>Samples per channel.</returns>
    [Benchmark]
    public long Decode()
    {
        _decoder.Reset();
        _synthesizer.Reset();

        long samples = 0;
        Span<Range> frames = _frameRanges;
        Span<float> floatPcm = _floatPcm;
        Span<short> pcm = _pcm;
        foreach (byte[] packet in _packets)
        {
            OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out OpusToc toc, out _);
            if (status != OpusStatus.Ok)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"vector {Vector:D2}: packet parse failed with {status}."));
            }

            int lm = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms2_5 => 0,
                OpusFrameDuration.Ms5 => 1,
                OpusFrameDuration.Ms10 => 2,
                _ => 3,
            };
            int endBand = toc.Bandwidth switch
            {
                OpusBandwidth.Narrowband => 13,
                OpusBandwidth.Mediumband => 17,
                OpusBandwidth.Wideband => 17,
                OpusBandwidth.SuperWideband => 19,
                _ => 21,
            };
            int streamChannels = toc.IsStereo ? 2 : 1;

            for (int i = 0; i < frameCount; i++)
            {
                Range range = frames[i];
                int size = range.End.Value - range.Start.Value;
                int frameSamples = CeltTables.ShortBlockSamples << lm;

                long start = Stopwatch.GetTimestamp();
                if (size <= 1)
                {
                    _synthesizer.DecodeLost(lm, startBand: 0, floatPcm[..(frameSamples * OutputChannels)]);
                    long concealed = Stopwatch.GetTimestamp();
                    _synthesisTicks += concealed - start;
                    start = concealed;
                }
                else
                {
                    var rangeDecoder = new RangeDecoder(packet.AsSpan(range));
                    _decoder.DecodeFrame(ref rangeDecoder, size, lm, 0, endBand, streamChannels, _frame);
                    frameSamples = _frame.SampleCount;
                    long decoded = Stopwatch.GetTimestamp();
                    _synthesizer.Synthesize(_frame, floatPcm[..(frameSamples * OutputChannels)]);
                    long synthesised = Stopwatch.GetTimestamp();
                    _decodeTicks += decoded - start;
                    _synthesisTicks += synthesised - decoded;
                    start = synthesised;
                }

                int count = frameSamples * OutputChannels;
                for (int j = 0; j < count; j++)
                {
                    pcm[j] = CeltPcm.ToInt16(floatPcm[j]);
                }

                long converted = Stopwatch.GetTimestamp();
                _convertTicks += converted - start;
                _frameCount++;
                samples += frameSamples;
            }
        }

        return samples;
    }

    /// <summary>Writes the accumulated stage breakdown.</summary>
    [GlobalCleanup]
    public void Report()
    {
        double microsecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
        double frames = Math.Max(1, _frameCount);
        Console.WriteLine(
            FormattableString.Invariant(
                $"CELT-STAGES vector={Vector:D2} frames={_frameCount} decode={_decodeTicks * microsecondsPerTick / frames:F2}us synth={_synthesisTicks * microsecondsPerTick / frames:F2}us int16={_convertTicks * microsecondsPerTick / frames:F2}us"));
    }
}

/// <summary>
/// Breaks a SILK-only vector down into parameter + excitation decode and synthesis at the
/// SILK internal rate.
/// </summary>
[MemoryDiagnoser]
public class SilkDecodeStageBenchmarks
{
    private const int MaxPcmSamples = 2 * 3 * 320;

    private byte[][] _packets = [];
    private Range[] _frameRanges = [];
    private SilkDecoder _decoder = null!;
    private SilkSynthesis _synthesizer = null!;
    private SilkFrameResult _result = null!;
    private short[] _pcm = [];
    private int _rateHz;

    private long _decodeTicks;
    private long _synthesisTicks;
    private long _frameCount;

    /// <summary>Gets or sets the RFC 8251 vector number to decode.</summary>
    [Params(2, 3, 4)]
    public int Vector { get; set; }

    /// <summary>Reads the vector into memory and constructs the reusable decoder state.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _rateHz = Vector switch
        {
            2 => 8000,
            3 => 12000,
            _ => 16000,
        };
        _packets = TestVectorData.ReadPackets(Vector);
        _frameRanges = new Range[OpusPacket.MaxFrames];
        _decoder = new SilkDecoder();
        _synthesizer = new SilkSynthesis();
        _result = new SilkFrameResult();
        _pcm = new short[MaxPcmSamples];
        _decoder.Reset();
        _synthesizer.Reset();
    }

    /// <summary>Decodes the whole vector, accumulating the per-stage cost.</summary>
    /// <returns>Samples per channel.</returns>
    [Benchmark]
    public long Decode()
    {
        _decoder.Reset();
        _synthesizer.Reset();

        long samples = 0;
        Span<Range> frames = _frameRanges;
        foreach (byte[] packet in _packets)
        {
            OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out OpusToc toc, out _);
            if (status != OpusStatus.Ok)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"vector {Vector:D2}: packet parse failed with {status}."));
            }

            int durationMs = toc.FrameDuration switch
            {
                OpusFrameDuration.Ms10 => 10,
                OpusFrameDuration.Ms20 => 20,
                OpusFrameDuration.Ms40 => 40,
                _ => 60,
            };
            int channels = toc.IsStereo ? 2 : 1;

            for (int i = 0; i < frameCount; i++)
            {
                long start = Stopwatch.GetTimestamp();
                var rangeDecoder = new RangeDecoder(packet.AsSpan(frames[i]));
                _decoder.DecodeFrame(ref rangeDecoder, _rateHz, channels, durationMs, _result);
                long decoded = Stopwatch.GetTimestamp();
                _synthesizer.Synthesize(_result, _pcm);
                long synthesised = Stopwatch.GetTimestamp();

                _decodeTicks += decoded - start;
                _synthesisTicks += synthesised - decoded;
                _frameCount++;
                samples += durationMs * _rateHz / 1000;
            }
        }

        return samples;
    }

    /// <summary>Writes the accumulated stage breakdown.</summary>
    [GlobalCleanup]
    public void Report()
    {
        double microsecondsPerTick = 1_000_000.0 / Stopwatch.Frequency;
        double frames = Math.Max(1, _frameCount);
        Console.WriteLine(
            FormattableString.Invariant(
                $"SILK-STAGES vector={Vector:D2} frames={_frameCount} decode={_decodeTicks * microsecondsPerTick / frames:F2}us synth={_synthesisTicks * microsecondsPerTick / frames:F2}us"));
    }
}
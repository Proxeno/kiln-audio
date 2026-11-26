using BenchmarkDotNet.Attributes;
using Kiln.Audio.Celt;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;

namespace Kiln.Audio.Benchmarks;

/// <summary>
/// Decodes the CELT-only RFC 8251 vectors (01, 07 and 11) end to end: packet framing, CELT
/// bitstream decode, synthesis to 48 kHz PCM and the 16-bit conversion the driver performs.
/// </summary>
/// <remarks>
/// Every invocation decodes the whole vector from packets that <c>[GlobalSetup]</c> has already
/// read into memory, so no file IO is measured. The decoder and synthesizer are
/// reset at the start of each invocation to replay the stream from its initial state.
/// </remarks>
[MemoryDiagnoser]
public class CeltDecodeBenchmarks
{
    private const int OutputChannels = 2;

    private byte[][] _packets = [];
    private Range[] _frameRanges = [];
    private CeltDecoder _decoder = null!;
    private CeltSynthesizer _synthesizer = null!;
    private CeltFrameResult _frame = null!;
    private float[] _floatPcm = [];
    private short[] _pcm = [];

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

    /// <summary>Decodes the whole vector and returns the number of 48 kHz samples per channel.</summary>
    /// <returns>Samples per channel, which the report converts to audio duration.</returns>
    [Benchmark]
    public long Decode()
    {
        _decoder.Reset();
        _synthesizer.Reset();

        long samples = 0;
        foreach (byte[] packet in _packets)
        {
            DecodePacket(packet, ref samples);
        }

        return samples;
    }

    private void DecodePacket(byte[] packet, ref long samples)
    {
        Span<Range> frames = _frameRanges;
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
            OpusFrameDuration.Ms20 => 3,
            _ => throw new InvalidOperationException("The CELT vectors expect a 2.5 to 20 ms frame."),
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

        Span<float> floatPcm = _floatPcm;
        Span<short> pcm = _pcm;
        for (int i = 0; i < frameCount; i++)
        {
            Range range = frames[i];
            int size = range.End.Value - range.Start.Value;
            int frameSamples = CeltTables.ShortBlockSamples << lm;
            if (size <= 1)
            {
                _synthesizer.DecodeLost(lm, startBand: 0, floatPcm[..(frameSamples * OutputChannels)]);
            }
            else
            {
                var rangeDecoder = new RangeDecoder(packet.AsSpan(range));
                _decoder.DecodeFrame(ref rangeDecoder, size, lm, 0, endBand, streamChannels, _frame);
                frameSamples = _frame.SampleCount;
                _synthesizer.Synthesize(_frame, floatPcm[..(frameSamples * OutputChannels)]);
            }

            int count = frameSamples * OutputChannels;
            for (int j = 0; j < count; j++)
            {
                pcm[j] = CeltPcm.ToInt16(floatPcm[j]);
            }

            samples += frameSamples;
        }
    }
}

/// <summary>
/// Decodes the SILK-only RFC 8251 vectors at the SILK internal rate: 02 narrowband (8 kHz),
/// 03 mediumband (12 kHz) and 04 wideband (16 kHz).
/// </summary>
/// <remarks>
/// Each packet's TOC supplies the channel count and frame duration, as the SILK conformance
/// driver does. The output is the internal-rate PCM that libopus feeds to its resampler, so
/// the reported speed is measured against the same audio duration. No file IO is measured.
/// </remarks>
[MemoryDiagnoser]
public class SilkDecodeBenchmarks
{
    private const int MaxPcmSamples = 2 * 3 * 320;

    private byte[][] _packets = [];
    private Range[] _frameRanges = [];
    private SilkDecoder _decoder = null!;
    private SilkSynthesis _synthesizer = null!;
    private SilkFrameResult _result = null!;
    private short[] _pcm = [];
    private int _rateHz;

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
            4 => 16000,
            _ => throw new InvalidOperationException(
                FormattableString.Invariant($"Vector {Vector} is not a SILK-only vector.")),
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

    /// <summary>Decodes the whole vector and returns the number of internal-rate samples per channel.</summary>
    /// <returns>Samples per channel, which the report converts to audio duration.</returns>
    [Benchmark]
    public long Decode()
    {
        _decoder.Reset();
        _synthesizer.Reset();

        long samples = 0;
        foreach (byte[] packet in _packets)
        {
            DecodePacket(packet, ref samples);
        }

        return samples;
    }

    private void DecodePacket(byte[] packet, ref long samples)
    {
        Span<Range> frames = _frameRanges;
        OpusStatus status = OpusPacket.TryParse(packet, frames, out int frameCount, out OpusToc toc, out _);
        if (status != OpusStatus.Ok)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"vector {Vector:D2}: packet parse failed with {status}."));
        }

        if (toc.Mode != OpusMode.Silk)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"vector {Vector:D2}: expected a SILK-only stream, found {toc.Mode}."));
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
            var rangeDecoder = new RangeDecoder(packet.AsSpan(frames[i]));
            _decoder.DecodeFrame(ref rangeDecoder, _rateHz, channels, durationMs, _result);
            _synthesizer.Synthesize(_result, _pcm);
            samples += durationMs * _rateHz / 1000;
        }
    }
}

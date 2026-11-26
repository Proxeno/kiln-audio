using System.Text;
using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Plc;

/// <summary>
/// Exercises in-band FEC (LBRR) decoding against libopus and against packet loss concealment.
/// </summary>
/// <remarks>
/// A SILK WB mono stream is encoded with <c>-inbandfec -loss 10</c>, which puts LBRR data for the
/// previous frame into every packet. One packet is dropped. The recovery of that frame from the
/// next packet must be bit-exact with libopus's FEC decode at the internal rate, and at 48 kHz it
/// must be closer to the clean decode than concealment is.
/// </remarks>
public class PlcFecTests
{
    private const int ApiRate = 48000;
    private const int FrameMs = 20;

    private readonly ITestOutputHelper _output;

    public PlcFecTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Fec_recovery_is_bit_exact_and_beats_concealment()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            _output.WriteLine("libopus oracle not available; skipping.");
            return;
        }

        List<PlcPacket> packets = EncodeSilkWithFec();
        packets.Should().HaveCountGreaterThan(20);

        OpusToc first = OpusToc.Parse(packets[0].Data[0]);
        int internalRate = first.Bandwidth switch
        {
            OpusBandwidth.Narrowband => 8000,
            OpusBandwidth.Mediumband => 12000,
            _ => 16000,
        };

        foreach (PlcPacket packet in packets)
        {
            OpusToc toc = OpusToc.Parse(packet.Data[0]);
            toc.Mode.Should().Be(OpusMode.Silk);
            toc.Bandwidth.Should().Be(first.Bandwidth, $"the stream must keep one SILK bandwidth");
            toc.IsStereo.Should().BeFalse();
            toc.FrameDuration.Should().Be(OpusFrameDuration.Ms20);
        }

        int dropIndex = FindDropIndex(packets, internalRate);
        dropIndex.Should().BeGreaterThanOrEqualTo(10, $"the stream must contain recoverable LBRR frames");

        short[] oracle = LibOpusOracle.Decode(
            BuildSingleDropFile(packets, dropIndex), internalRate, 1, "-inbandfec");
        (short[] ours, int fecStart) = DecodeFecInternal(packets, dropIndex, internalRate);
        int frameSamples = internalRate * FrameMs / 1000;

        // opus_demo with in-band FEC delays its output, so align on the frames before the drop,
        // which are unaffected by the FEC decision.
        int dropSample = dropIndex * frameSamples;
        int offset = FindPrefixOffset(ours, oracle, dropSample);
        int firstMismatch = FirstMismatch(ours.AsSpan(0, dropSample), oracle.AsSpan(offset));
        firstMismatch.Should().Be(
            -1, $"the frames before the drop must be bit-exact with libopus");
        _output.WriteLine(FormattableString.Invariant(
            $"{internalRate} Hz mono: {packets.Count} packets, alignment {offset}"));

        // The recovered frame is the only copy of frame N (its packet was replaced by a
        // lost-packet marker), so an exact 640/320-sample match is the FEC decode.
        int fecMatch = FindExactMatch(oracle, ours.AsSpan(fecStart, frameSamples));
        fecMatch.Should().BeGreaterThanOrEqualTo(
            0, $"the FEC decode must be bit-exact with libopus at the internal rate");

        double fecDb = FecVersusPlcAt48k(packets, dropIndex, out double plcDb, out int apiFrameSamples);
        _output.WriteLine(FormattableString.Invariant(
            $"48 kHz frame {dropIndex} ({apiFrameSamples} samples): FEC {fecDb:F2} dB, PLC {plcDb:F2} dB"));
        fecDb.Should().BeGreaterThan(plcDb, $"in-band FEC must beat concealment");
    }

    /// <summary>Encodes a SILK mono stream with in-band FEC, from the start of vector 04.</summary>
    private static List<PlcPacket> EncodeSilkWithFec() =>
        EncodeStream(["-bandwidth", "WB", "-framesize", "20", "-cbr", "-inbandfec", "-loss", "10"]);

    private static short[] MonoInput()
    {
        short[] stereo = Pcm16.Read(TestVectors.DecPath(4));
        int samples = Math.Min(stereo.Length / 2, ApiRate * 4);
        var mono = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            mono[i] = (short)((stereo[2 * i] + stereo[(2 * i) + 1]) / 2);
        }

        return mono;
    }

    private static List<PlcPacket> EncodeStream(string[] extraArgs)
    {
        short[] mono = MonoInput();
        byte[] bitFile = LibOpusOracle.Encode(mono, ApiRate, 1, 32000, extraArgs);

        string path = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-plc-fec-{Guid.NewGuid():N}.bit"));
        try
        {
            File.WriteAllBytes(path, bitFile);
            var packets = new List<PlcPacket>();
            foreach ((byte[] packet, uint finalRange) in OpusBitFile.Read(path))
            {
                packets.Add(new PlcPacket(packet, finalRange));
            }

            return packets;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Returns the bytes of a single-frame packet's only frame (excluding the TOC).</summary>
    private static byte[] FirstFrameData(byte[] packet)
    {
        Span<Range> frames = stackalloc Range[OpusPacket.MaxFrames];
        OpusStatus status = OpusPacket.TryParse(packet, frames, out int count, out _, out _);
        if (status != OpusStatus.Ok || count != 1)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"expected a single-frame packet, got {status} with {count} frames"));
        }

        return packet.AsSpan(frames[0]).ToArray();
    }

    /// <summary>Finds a packet whose successor carries LBRR data for it.</summary>
    private static int FindDropIndex(IReadOnlyList<PlcPacket> packets, int internalRate)
    {
        for (int i = 12; i + 1 < packets.Count; i++)
        {
            // The LBRR presence bits precede any state-dependent symbol, so a fresh decoder reads
            // them exactly.
            var decoder = new SilkDecoder();
            var result = new SilkFrameResult();
            var dec = new RangeDecoder(FirstFrameData(packets[i].Data));
            decoder.DecodeFrame(ref dec, internalRate, 1, FrameMs, result, lbrrOnly: true);
            if (result.IsLbrrCoded(0, 0))
            {
                return i - 1;
            }
        }

        return -1;
    }

    /// <summary>Decodes the internal-rate stream with the dropped frame recovered via FEC.</summary>
    private static (short[] Pcm, int FecStart) DecodeFecInternal(
        IReadOnlyList<PlcPacket> packets, int dropIndex, int internalRate)
    {
        var decoder = new SilkDecoder();
        var synthesis = new SilkSynthesis();
        var result = new SilkFrameResult();
        int frameSamples = internalRate * FrameMs / 1000;
        var pcm = new List<short>(packets.Count * frameSamples);
        var frame = new short[frameSamples];
        int fecStart = -1;

        for (int i = 0; i < packets.Count; i++)
        {
            if (i == dropIndex + 1)
            {
                var dec = new RangeDecoder(FirstFrameData(packets[i].Data));
                decoder.DecodeFrame(ref dec, internalRate, 1, FrameMs, result, lbrrOnly: true);
                synthesis.SynthesizeFec(result, frame);
                fecStart = pcm.Count;
                pcm.AddRange(frame);
            }

            if (i == dropIndex)
            {
                continue;
            }

            var normal = new RangeDecoder(FirstFrameData(packets[i].Data));
            decoder.DecodeFrame(ref normal, internalRate, 1, FrameMs, result);
            synthesis.Synthesize(result, frame);
            pcm.AddRange(frame);
        }

        return (pcm.ToArray(), fecStart);
    }

    /// <summary>Decodes at 48 kHz and compares FEC with concealment against the clean decode.</summary>
    private static double FecVersusPlcAt48k(
        IReadOnlyList<PlcPacket> packets, int dropIndex, out double plcDb, out int frameSamples)
    {
        var options = new OpusDecoderOptions { SampleRate = ApiRate, Channels = 1 };
        frameSamples = ApiRate * FrameMs / 1000;
        var scratch = new short[ApiRate * 120 / 1000];

        var reference = new List<short>();
        using (var decoder = new OpusDecoder(options))
        {
            foreach (PlcPacket packet in packets)
            {
                int produced = decoder.Decode(packet.Data, scratch);
                reference.AddRange(scratch.AsSpan(0, produced).ToArray());
            }
        }

        var fec = new List<short>();
        using (var decoder = new OpusDecoder(options))
        {
            for (int i = 0; i < packets.Count; i++)
            {
                if (i == dropIndex + 1)
                {
                    int produced = decoder.Decode(packets[i].Data, scratch, decodeFec: true);
                    fec.AddRange(scratch.AsSpan(0, produced).ToArray());
                }

                if (i == dropIndex)
                {
                    continue;
                }

                int samples = decoder.Decode(packets[i].Data, scratch);
                fec.AddRange(scratch.AsSpan(0, samples).ToArray());
            }
        }

        var plc = new List<short>();
        using (var decoder = new OpusDecoder(options))
        {
            for (int i = 0; i < packets.Count; i++)
            {
                int samples;
                if (i == dropIndex)
                {
                    samples = decoder.DecodeLoss(scratch);
                }
                else
                {
                    samples = decoder.Decode(packets[i].Data, scratch);
                }

                plc.AddRange(scratch.AsSpan(0, samples).ToArray());
            }
        }

        int windowStart = dropIndex * frameSamples;
        int length = Math.Min(frameSamples, reference.Count - windowStart);
        double fecSnr = PlcTestData.Snr(
            reference.ToArray(), fec.ToArray(), windowStart, length);
        plcDb = PlcTestData.Snr(reference.ToArray(), plc.ToArray(), windowStart, length);
        return fecSnr;
    }

    /// <summary>Writes the packet file with one packet replaced by a lost-packet marker.</summary>
    private static byte[] BuildSingleDropFile(IReadOnlyList<PlcPacket> packets, int dropIndex)
    {
        var losses = new HashSet<int> { dropIndex };
        return PlcTestData.BuildOracleLossFile(packets, losses);
    }

    /// <summary>Finds the offset in <paramref name="reference"/> that best matches the prefix.</summary>
    private static int FindPrefixOffset(short[] ours, short[] reference, int length)
    {
        int limit = Math.Min(reference.Length, 2000);
        int bestOffset = 0;
        int bestPrefix = -1;
        for (int offset = 0; offset < limit; offset++)
        {
            int n = 0;
            while (n < length && offset + n < reference.Length && ours[n] == reference[offset + n])
            {
                n++;
            }

            if (n > bestPrefix)
            {
                bestPrefix = n;
                bestOffset = offset;
            }
        }

        return bestOffset;
    }

    /// <summary>Returns the first offset where <paramref name="pattern"/> occurs exactly, or -1.</summary>
    private static int FindExactMatch(short[] reference, ReadOnlySpan<short> pattern)
    {
        for (int offset = 0; offset + pattern.Length <= reference.Length; offset++)
        {
            if (reference.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
            {
                return offset;
            }
        }

        return -1;
    }

    private static int FirstMismatch(ReadOnlySpan<short> ours, ReadOnlySpan<short> reference)
    {
        int length = Math.Min(ours.Length, reference.Length);
        for (int n = 0; n < length; n++)
        {
            if (ours[n] != reference[n])
            {
                return n;
            }
        }

        return -1;
    }
}

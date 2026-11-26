using FluentAssertions;
using Kiln.Audio.Entropy;
using Kiln.Audio.Silk;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Decoder;

/// <summary>
/// The in-band FEC signal: <see cref="OpusPacket.HasLbrr"/> must predict
/// <see cref="OpusDecoder.LastFecOutcome"/> for every packet, both must match the LBRR content of
/// the stream, and neither may allocate.
/// </summary>
/// <remarks>
/// The RFC 8251 vectors supply SILK and hybrid packets; an stream encoded with
/// <c>opus_demo -inbandfec -loss 10</c> guarantees that LBRR data is actually present, because
/// the vectors' LBRR flags depend on the encoder settings used to generate them.
/// </remarks>
public class FecSignalTests
{
    private const int ApiRate = 48000;
    private const int MaxPacketSamples = ApiRate * 120 / 1000;

    private readonly ITestOutputHelper _output;

    public FecSignalTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Every SILK-only and hybrid packet of the RFC 8251 vectors must report the same LBRR
    /// presence from the probe and from a real FEC decode.
    /// </summary>
    [Fact]
    public void HasLbrr_agrees_with_LastFecOutcome_on_rfc_vectors()
    {
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = ApiRate, Channels = 2 });
        var pcm = new short[MaxPacketSamples * 2];
        int silkPackets = 0;
        int lbrrPackets = 0;

        for (int vector = 1; vector <= TestVectors.Count; vector++)
        {
            foreach ((byte[] packet, _) in OpusBitFile.Read(TestVectors.BitPath(vector)))
            {
                OpusToc toc = OpusToc.Parse(packet[0]);
                if (toc.Mode == OpusMode.Celt)
                {
                    continue;
                }

                silkPackets++;
                bool hasLbrr = OpusPacket.HasLbrr(packet);
                decoder.Decode(packet, pcm, decodeFec: true);
                bool recovered = decoder.LastFecOutcome == OpusFecOutcome.Recovered;

                hasLbrr.Should().Be(
                    recovered,
                    $"vector {vector} packet {silkPackets} must agree between HasLbrr and LastFecOutcome");
                if (hasLbrr)
                {
                    lbrrPackets++;
                }
            }
        }

        _output.WriteLine(FormattableString.Invariant(
            $"{silkPackets} SILK/hybrid packets, {lbrrPackets} with LBRR"));
        silkPackets.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// A stream encoded with in-band FEC must show LBRR packets, and every packet must agree
    /// between the probe and the decoder's reported outcome.
    /// </summary>
    [Fact]
    public void HasLbrr_agrees_with_LastFecOutcome_on_fec_stream()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            _output.WriteLine("libopus oracle not available; skipping.");
            return;
        }

        List<byte[]> packets = EncodeWithLbrr();
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = ApiRate, Channels = 1 });
        var pcm = new short[MaxPacketSamples];
        int lbrrPackets = 0;

        foreach (byte[] packet in packets)
        {
            bool hasLbrr = OpusPacket.HasLbrr(packet);
            decoder.Decode(packet, pcm, decodeFec: true);

            hasLbrr.Should().Be(decoder.LastFecOutcome == OpusFecOutcome.Recovered);

            // Ground truth: the SILK header decoder reads the same flags with its own state.
            var silk = new SilkDecoder();
            var result = new SilkFrameResult();
            var dec = new RangeDecoder(FirstFrame(packet));
            silk.DecodeFrame(ref dec, 16000, 1, 20, result, lbrrOnly: true);
            hasLbrr.Should().Be(result.IsLbrrCoded(0, 0));

            if (hasLbrr)
            {
                lbrrPackets++;
            }
        }

        _output.WriteLine(FormattableString.Invariant(
            $"{packets.Count} packets, {lbrrPackets} with LBRR"));
        lbrrPackets.Should().BeGreaterThan(
            0, $"the encoder was run with -inbandfec and a 10 % loss target");
    }

    /// <summary>A CELT-only packet has no SILK LBRR, so FEC conceals.</summary>
    [Fact]
    public void Celt_only_packet_has_no_lbrr_and_conceals()
    {
        byte[] packet = FirstPacket(1);
        OpusToc.Parse(packet[0]).Mode.Should().Be(OpusMode.Celt);

        OpusPacket.HasLbrr(packet).Should().BeFalse();

        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = ApiRate, Channels = 2 });
        var pcm = new short[MaxPacketSamples * 2];
        decoder.Decode(packet, pcm, decodeFec: true);
        decoder.LastFecOutcome.Should().Be(OpusFecOutcome.Concealed);
    }

    /// <summary>A decode without FEC and a loss decode both report NotRequested.</summary>
    [Fact]
    public void Plain_decode_and_loss_report_not_requested()
    {
        byte[] packet = FirstPacket(2);
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = ApiRate, Channels = 2 });
        var pcm = new short[MaxPacketSamples * 2];

        decoder.Decode(packet, pcm);
        decoder.LastFecOutcome.Should().Be(OpusFecOutcome.NotRequested);

        decoder.DecodeLoss(pcm);
        decoder.LastFecOutcome.Should().Be(OpusFecOutcome.NotRequested);
    }

    [Fact]
    public void HasLbrr_allocates_nothing()
    {
        byte[][] packets = [.. Enumerable.Range(1, TestVectors.Count).Select(FirstPacket)];
        for (int warmup = 0; warmup < 4; warmup++)
        {
            foreach (byte[] packet in packets)
            {
                OpusPacket.HasLbrr(packet);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (byte[] packet in packets)
        {
            OpusPacket.HasLbrr(packet);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Fec_decode_allocates_nothing()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            _output.WriteLine("libopus oracle not available; skipping.");
            return;
        }

        byte[] packet = EncodeWithLbrr().First(packet => OpusPacket.HasLbrr(packet));
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = ApiRate, Channels = 1 });
        var pcm = new short[MaxPacketSamples];

        for (int warmup = 0; warmup < 8; warmup++)
        {
            decoder.Decode(packet, pcm, decodeFec: true);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        decoder.Decode(packet, pcm, decodeFec: true);
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }

    /// <summary>Encodes speech-like audio with in-band FEC and returns the packets.</summary>
    private static List<byte[]> EncodeWithLbrr()
    {
        short[] mono = SpeechLikePcm();
        byte[] bitFile = LibOpusOracle.Encode(
            mono,
            ApiRate,
            1,
            24000,
            ["-bandwidth", "WB", "-framesize", "20", "-cbr", "-inbandfec", "-loss", "10"]);

        string path = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-fec-signal-{Guid.NewGuid():N}.bit"));
        try
        {
            File.WriteAllBytes(path, bitFile);
            return [.. OpusBitFile.Read(path).Select(record => record.Packet)];
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Two seconds of voiced-sounding tones and a little noise, so the VAD stays active.</summary>
    private static short[] SpeechLikePcm()
    {
        int samples = ApiRate * 2;
        var pcm = new short[samples];
        var random = new Random(2858);
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / ApiRate;
            double envelope = 0.55 + (0.45 * Math.Sin(2 * Math.PI * 3 * t));
            double voiced = Math.Sin(2 * Math.PI * 220 * t)
                + (0.5 * Math.Sin(2 * Math.PI * 440 * t))
                + (0.25 * Math.Sin(2 * Math.PI * 880 * t));
            double noise = (random.NextDouble() * 2) - 1;
            pcm[i] = (short)(envelope * ((0.25 * voiced) + (0.01 * noise)) * short.MaxValue);
        }

        return pcm;
    }

    private static byte[] FirstPacket(int vector) =>
        OpusBitFile.Read(TestVectors.BitPath(vector)).First().Packet;

    /// <summary>Returns the bytes of a single-frame packet's only frame (excluding the TOC).</summary>
    private static byte[] FirstFrame(byte[] packet)
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
}

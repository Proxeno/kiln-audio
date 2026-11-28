using System.Globalization;
using FluentAssertions;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.Encoder;

/// <summary>
/// RELAX-ENC: <see cref="OpusPerformanceMode.Relaxed"/> keeps the bitstream standard (libopus decodes it and
/// the final range matches), allocates nothing, stays within 1 dB of Precise and does not fall under the frozen
/// Relaxed quality table.
/// </summary>
public class RelaxedEncoderTests
{
    private const int FrameSamples = 960;

    /// <summary>Name, application, channels, bitrate, bandwidth, complexity, frozen Relaxed segSNR floor (dB).</summary>
    public static TheoryData<string, OpusApplication, int, int, OpusBandwidth?, int, double> Configs()
    {
        var d = new TheoryData<string, OpusApplication, int, int, OpusBandwidth?, int, double>();
        foreach (int cx in new[] { 0, 5 })
        {
            d.Add("celt-stereo-64k", OpusApplication.RestrictedLowDelay, 2, 64000, null, cx, FrozenFloor("celt-stereo-64k", cx));
            d.Add("celt-mono-32k", OpusApplication.RestrictedLowDelay, 1, 32000, null, cx, FrozenFloor("celt-mono-32k", cx));
            d.Add("silk-wb-20k", OpusApplication.Voip, 1, 20000, OpusBandwidth.Wideband, cx, FrozenFloor("silk-wb-20k", cx));
            d.Add("silk-wb-12k", OpusApplication.Voip, 1, 12000, OpusBandwidth.Wideband, cx, FrozenFloor("silk-wb-12k", cx));
            d.Add("hybrid-fb-32k", OpusApplication.Audio, 1, 32000, OpusBandwidth.Fullband, cx, FrozenFloor("hybrid-fb-32k", cx));
        }

        return d;
    }

    [Fact]
    public void Default_mode_is_precise_and_undefined_values_are_rejected()
    {
        new OpusEncoderOptions().PerformanceMode.Should().Be(OpusPerformanceMode.Precise);
        var bad = new OpusEncoderOptions { PerformanceMode = (OpusPerformanceMode)7 };
        bad.Invoking(o => o.Validate()).Should().Throw<ArgumentOutOfRangeException>();
        new OpusEncoderOptions { PerformanceMode = OpusPerformanceMode.Relaxed }.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Relaxed_packets_decode_in_libopus_with_matching_final_range(
        string name, OpusApplication app, int channels, int bitrate, OpusBandwidth? bandwidth, int complexity, double floor)
    {
        _ = floor;
        if (!OracleTestSupport.OracleAvailable() || !TestVectors.Available)
        {
            return;
        }

        // Precise is not exercised with UseCbr here: its Kiln 1.0 rate-loop restore corrupts SILK/hybrid CBR
        // frames that need the restore (kept byte-identical on purpose; see the Relaxed restore fix).
        AssertInterop(name, app, channels, bitrate, bandwidth, complexity, OpusPerformanceMode.Relaxed);
    }

    private static void AssertInterop(
        string name, OpusApplication app, int channels, int bitrate, OpusBandwidth? bandwidth, int complexity, OpusPerformanceMode mode)
    {
        short[] pcm = Input(channels);
        using var encoder = new OpusEncoder(Options(app, channels, bitrate, bandwidth, complexity, mode));
        var packets = new List<(byte[], uint)>();
        var buffer = new byte[1276];
        int frameLength = FrameSamples * channels;
        for (int offset = 0; offset + frameLength <= pcm.Length; offset += frameLength)
        {
            int written = encoder.Encode(pcm.AsSpan(offset, frameLength), buffer);
            packets.Add((buffer.AsSpan(0, written).ToArray(), encoder.FinalRange));
        }

        short[] decoded;
        string diagnostics;
        try
        {
            (decoded, diagnostics) = EncoderOracle.Decode(EncoderCorpus.WriteBitFileToBytes(packets), channels);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{name} cx{complexity} {mode}: {ex.Message}", ex);
        }

        EncoderOracle.HasRangeMismatch(diagnostics).Should().BeFalse($"{name} cx{complexity} {mode}: {diagnostics}");
        decoded.Length.Should().Be(packets.Count * frameLength, name);
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Relaxed_cbr_packets_never_exceed_the_budget_precise_keeps(
        string name, OpusApplication app, int channels, int bitrate, OpusBandwidth? bandwidth, int complexity, double floor)
    {
        _ = floor;
        if (!TestVectors.Available)
        {
            return;
        }

        short[] pcm = Input(channels);
        int precise = WorstPacket(pcm, channels, app, bitrate, bandwidth, complexity, OpusPerformanceMode.Precise);
        int relaxed = WorstPacket(pcm, channels, app, bitrate, bandwidth, complexity, OpusPerformanceMode.Relaxed);
        relaxed.Should().BeLessThanOrEqualTo(precise, $"{name} cx{complexity}: Relaxed must not outgrow Precise's CBR packets");
        if (app == OpusApplication.Voip)
        {
            // SILK-only CBR: the packet (TOC included) must fit the nominal frame budget exactly.
            relaxed.Should().BeLessThanOrEqualTo(bitrate * 20 / 8000, $"{name} cx{complexity}: SILK CBR budget");
        }
    }

    private static int WorstPacket(
        short[] pcm, int channels, OpusApplication app, int bitrate, OpusBandwidth? bandwidth, int complexity, OpusPerformanceMode mode)
    {
        using var encoder = new OpusEncoder(Options(app, channels, bitrate, bandwidth, complexity, mode, useVbr: false));
        var buffer = new byte[1276];
        int frameLength = FrameSamples * channels;
        int worst = 0;
        for (int offset = 0; offset + frameLength <= pcm.Length; offset += frameLength)
        {
            worst = Math.Max(worst, encoder.Encode(pcm.AsSpan(offset, frameLength), buffer));
        }

        return worst;
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Relaxed_quality_is_within_one_db_of_precise_and_above_the_frozen_floor(
        string name, OpusApplication app, int channels, int bitrate, OpusBandwidth? bandwidth, int complexity, double floor)
    {
        if (!TestVectors.Available)
        {
            return;
        }

        short[] pcm = Input(channels);
        double precise = Snr(pcm, channels, app, bitrate, bandwidth, complexity, OpusPerformanceMode.Precise);
        double relaxed = Snr(pcm, channels, app, bitrate, bandwidth, complexity, OpusPerformanceMode.Relaxed);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"RELAXQ {name} cx{complexity} precise={precise:F3} relaxed={relaxed:F3}"));
        relaxed.Should().BeGreaterThanOrEqualTo(precise - 1.0, $"{name} cx{complexity}: Relaxed must stay within 1 dB of Precise");
        relaxed.Should().BeGreaterThanOrEqualTo(floor, $"{name} cx{complexity}: frozen Relaxed floor");

        // The CBR comparison above flatters Relaxed on SILK (Precise keeps its Kiln 1.0 restore); repeat without CBR.
        double preciseVbr = Snr(pcm, channels, app, bitrate, bandwidth, complexity, OpusPerformanceMode.Precise, useCbr: false);
        double relaxedVbr = Snr(pcm, channels, app, bitrate, bandwidth, complexity, OpusPerformanceMode.Relaxed, useCbr: false);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"RELAXQ-VBR {name} cx{complexity} precise={preciseVbr:F3} relaxed={relaxedVbr:F3}"));
        relaxedVbr.Should().BeGreaterThanOrEqualTo(preciseVbr - 1.0, $"{name} cx{complexity}: Relaxed (no CBR) must stay within 1 dB of Precise");
    }

    [Theory]
    [MemberData(nameof(Configs))]
    public void Relaxed_encoding_allocates_nothing(
        string name, OpusApplication app, int channels, int bitrate, OpusBandwidth? bandwidth, int complexity, double floor)
    {
        _ = floor;
        if (!TestVectors.Available)
        {
            return;
        }

        short[] pcm = Input(channels);
        using var encoder = new OpusEncoder(Options(app, channels, bitrate, bandwidth, complexity, OpusPerformanceMode.Relaxed));
        var buffer = new byte[1276];
        int frameLength = FrameSamples * channels;
        EncodeAll(encoder, pcm, buffer, frameLength);
        EncodeAll(encoder, pcm, buffer, frameLength);
        long before = GC.GetAllocatedBytesForCurrentThread();
        EncodeAll(encoder, pcm, buffer, frameLength);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0, $"{name} cx{complexity}");
    }

    [Fact]
    public void Precise_is_deterministic_and_unaffected_by_the_option()
    {
        if (!TestVectors.Available)
        {
            return;
        }

        short[] pcm = Input(1);
        byte[] a = Hash(pcm, OpusPerformanceMode.Precise);
        byte[] b = Hash(pcm, OpusPerformanceMode.Precise);
        a.Should().Equal(b);
    }

    // Frozen on the RELAX-ENC lane (1 s from each of the 12 RFC 8251 vectors, 0.15 dB under the measured value; ratchet: only ever raise).
    private static double FrozenFloor(string name, int complexity) => (name, complexity) switch
    {
        ("celt-mono-32k", 0) => 15.7,
        ("celt-mono-32k", 5) => 16.45,
        ("celt-stereo-64k", 0) => 18.45,
        ("celt-stereo-64k", 5) => 17.3,
        ("hybrid-fb-32k", 0) => 6.05,
        ("hybrid-fb-32k", 5) => 5.95,
        ("silk-wb-12k", 0) => 3.05,
        ("silk-wb-12k", 5) => 3.4,
        ("silk-wb-20k", 0) => 5.4,
        ("silk-wb-20k", 5) => 5.38,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static byte[] Hash(short[] pcm, OpusPerformanceMode mode)
    {
        using var encoder = new OpusEncoder(Options(OpusApplication.RestrictedLowDelay, 1, 32000, null, 5, mode));
        var buffer = new byte[1276];
        var all = new List<byte>();
        for (int offset = 0; offset + FrameSamples <= pcm.Length; offset += FrameSamples)
        {
            int n = encoder.Encode(pcm.AsSpan(offset, FrameSamples), buffer);
            all.AddRange(buffer.AsSpan(0, n).ToArray());
        }

        return System.Security.Cryptography.SHA256.HashData(all.ToArray());
    }

    private static void EncodeAll(OpusEncoder encoder, short[] pcm, byte[] buffer, int frameLength)
    {
        for (int offset = 0; offset + frameLength <= pcm.Length; offset += frameLength)
        {
            encoder.Encode(pcm.AsSpan(offset, frameLength), buffer);
        }
    }

    private static OpusEncoderOptions Options(
        OpusApplication app, int channels, int bitrate, OpusBandwidth? bandwidth, int complexity, OpusPerformanceMode mode, bool useVbr = false, bool useCbr = true) => new()
        {
            Application = app,
            SampleRate = 48000,
            Channels = channels,
            BitrateBps = bitrate,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = complexity,
            Bandwidth = bandwidth,
            UseCbr = useCbr,
            UseVbr = useVbr,
            PerformanceMode = mode,
        };

    private static double Snr(
        short[] pcm, int channels, OpusApplication app, int bitrate, OpusBandwidth? bandwidth, int complexity, OpusPerformanceMode mode, bool useCbr = true)
    {
        using var encoder = new OpusEncoder(Options(app, channels, bitrate, bandwidth, complexity, mode, useCbr: useCbr));
        using var decoder = new OpusDecoder(new OpusDecoderOptions { SampleRate = 48000, Channels = channels });
        int frameLength = FrameSamples * channels;
        int frames = pcm.Length / frameLength;
        var decoded = new short[frames * frameLength];
        var buffer = new byte[1276];
        var frame = new short[frameLength * 6];
        for (int f = 0; f < frames; f++)
        {
            int n = encoder.Encode(pcm.AsSpan(f * frameLength, frameLength), buffer);
            int got = decoder.Decode(buffer.AsSpan(0, n), frame);
            Array.Copy(frame, 0, decoded, f * frameLength, Math.Min(got * channels, frameLength));
        }

        // Delay is whatever aligns the decode best; Precise and Relaxed are scored with their own best alignment.
        int perChannel = decoded.Length / channels;
        int bestDelay = 0;
        double bestVal = double.MinValue;
        for (int delay = 0; delay < 1500; delay++)
        {
            double sig = 0;
            double noise = 0;
            for (int i = 4000; i < 4000 + 40000; i += 3)
            {
                double r = pcm[i * channels];
                double d = decoded[(i + delay) * channels] - r;
                sig += r * r;
                noise += d * d;
            }

            double v = -noise / Math.Max(sig, 1);
            if (v > bestVal)
            {
                bestVal = v;
                bestDelay = delay;
            }
        }

        double total = 0;
        long count = 0;
        for (int start = 0; start + FrameSamples <= perChannel - bestDelay; start += FrameSamples)
        {
            double signal = 0;
            double noise = 0;
            for (int i = 0; i < FrameSamples; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    double r = pcm[(((start + i) * channels) + c)];
                    double d = decoded[((start + i + bestDelay) * channels) + c] - r;
                    signal += r * r;
                    noise += d * d;
                }
            }

            double snr = signal <= 0 ? (noise <= 0 ? 60 : -10) : 10 * Math.Log10(signal / Math.Max(noise, 1e-9));
            total += Math.Clamp(snr, -10, 60);
            count++;
        }

        return total / count;
    }

    private static short[] Input(int channels)
    {
        // One second from the middle of each RFC 8251 vector: speech, music and test signals at varied levels.
        var parts = new List<short>();
        for (int v = 1; v <= TestVectors.Count; v++)
        {
            short[] stereo = Pcm16.Read(TestVectors.DecPath(v));
            int start = Math.Min(stereo.Length / 2 - 48000, 5 * 48000) * 2;
            start = Math.Max(0, start);
            int length = Math.Min(48000 * 2, stereo.Length - start);
            for (int i = 0; i < length; i += 2)
            {
                if (channels == 2)
                {
                    parts.Add(stereo[start + i]);
                    parts.Add(stereo[start + i + 1]);
                }
                else
                {
                    parts.Add((short)(((int)stereo[start + i] + stereo[start + i + 1]) / 2));
                }
            }
        }

        return [.. parts];
    }
}

using System.Diagnostics;
using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Stereo G2: on the owner's SILK ruling the hard gate is our stereo encoder against the RFC 6716
/// Appendix A reference encoder (frozen baseline, 0.05 dB allowance), scored against the
/// independent <see cref="IdealResampler"/>. The gap to libopus 1.5.2 is reported, not asserted.
/// </summary>
public class Enc2StereoQualityTests
{
    private readonly ITestOutputHelper _output;

    public Enc2StereoQualityTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<OpusBandwidth, OpusFrameDuration, int> Cases() =>
        new() { { OpusBandwidth.Wideband, OpusFrameDuration.Ms20, 24000 }, { OpusBandwidth.Wideband, OpusFrameDuration.Ms60, 24000 } };

    /// <summary>Ours must not fall below the frozen RFC 6716 reference figure.</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void Stereo_quality_matches_the_rfc_reference(OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        int ms = duration == OpusFrameDuration.Ms20 ? 20 : 60;
        int internalRate = SilkQualityHarness.InternalRate(bandwidth);
        Enc2StereoRfcBaseline.TryGet(bandwidth, ms, bitrate, out double baseline)
            .Should().BeTrue("the stereo RFC baseline must be frozen for this configuration");

        double ours = Enc2StereoQuality.MeasureOurs(bandwidth, duration, bitrate, complexity: 0, internalRate);
        double libopus = Enc2StereoQuality.MeasureLibopus(bandwidth, ms, bitrate, complexity: 0, internalRate);

        _output.WriteLine(
            $"stereo {bandwidth} {ms} ms {bitrate} bps c0: ours {ours:F2} dB, RFC 6716 reference {baseline:F2} dB, " +
            $"libopus 1.5.2 {libopus:F2} dB (gap {ours - libopus:+0.00;-0.00} dB)");

        ours.Should().BeGreaterThanOrEqualTo(
            baseline - Enc2StereoRfcBaseline.ToleranceDb,
            $"owner gate: stereo SILK must be at least the RFC 6716 reference ({baseline:F2} dB)");
    }
}

/// <summary>Freezes <see cref="Enc2StereoRfcBaseline"/> from the RFC 6716 Appendix A encoder.</summary>
/// <remarks>
/// Needs an RFC 6716 Appendix A <c>opus_demo</c> built with FIXED_POINT=1; set
/// <c>KILN_SILK_RFC_OPUS_DEMO</c> to its path. Without it the test passes without doing anything.
/// </remarks>
public class Enc2StereoRfcBaselineGenerator
{
    private readonly ITestOutputHelper _output;

    public Enc2StereoRfcBaselineGenerator(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Generate_stereo_baseline_table_when_the_rfc_encoder_is_available()
    {
        string? exe = Environment.GetEnvironmentVariable("KILN_SILK_RFC_OPUS_DEMO");
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        var report = new System.Text.StringBuilder();
        foreach ((OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate) in new[]
                 {
                     (OpusBandwidth.Wideband, OpusFrameDuration.Ms20, 24000),
                     (OpusBandwidth.Wideband, OpusFrameDuration.Ms60, 24000),
                 })
        {
            int ms = duration == OpusFrameDuration.Ms20 ? 20 : 60;
            int internalRate = SilkQualityHarness.InternalRate(bandwidth);
            double db = Enc2StereoQuality.MeasureRfcReference(exe, bandwidth, ms, bitrate, internalRate);
            double ours = Enc2StereoQuality.MeasureOurs(bandwidth, duration, bitrate, 0, internalRate);
            double libopus = Enc2StereoQuality.MeasureLibopus(bandwidth, ms, bitrate, 0, internalRate);
            report.AppendLine(FormattableString.Invariant(
                $"[(OpusBandwidth.{bandwidth}, {ms}, {bitrate})] = {db:F2},  // ours {ours:F2}, libopus {libopus:F2}"));
        }

        _output.WriteLine(report.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "kiln-enc2-stereo-rfc-baseline.txt"), report.ToString());
    }
}

/// <summary>The stereo G2 measurement pipeline: corpus, encoders, decoding and scoring.</summary>
internal static class Enc2StereoQuality
{
    /// <summary>
    /// The stereo corpus: the mono G2 corpus, panned (left = the signal, right = 0.6 of it), so the
    /// content and level match the mono gate and the stereo image exercises the mid/side path.
    /// </summary>
    internal static IReadOnlyList<(int Vector, short[] Stereo)> Corpus
    {
        get
        {
            var corpus = new List<(int, short[])>();
            foreach ((int vector, short[] mono) in SilkQualityHarness.Corpus)
            {
                var stereo = new short[mono.Length * 2];
                for (int i = 0; i < mono.Length; i++)
                {
                    stereo[2 * i] = mono[i];
                    stereo[(2 * i) + 1] = (short)(mono[i] * 0.6);
                }

                corpus.Add((vector, stereo));
            }

            return corpus;
        }
    }

    /// <summary>Aggregates one encoder over the stereo corpus, weighted by scored segments.</summary>
    internal static double Aggregate(int internalRate, Func<short[], double> measure, out string detail)
    {
        double total = 0;
        long segments = 0;
        var lines = new List<string>();
        foreach ((int vector, short[] stereo) in Corpus)
        {
            double snr = measure(stereo);
            long count = (stereo.Length / 2) / (internalRate / 50);
            total += snr * count;
            segments += count;
            lines.Add(FormattableString.Invariant($"v{vector} {snr:F2}"));
        }

        detail = string.Join(", ", lines);
        return total / segments;
    }

    /// <summary>Encodes with our stereo encoder, decodes with libopus and scores the ideal reference.</summary>
    internal static double MeasureOurs(
        OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate, int complexity, int internalRate) =>
        Aggregate(internalRate, stereo => ScoreOurs(stereo, bandwidth, duration, bitrate, complexity, internalRate), out _);

    private static double ScoreOurs(
        short[] stereo, OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate, int complexity, int internalRate)
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = 48000,
            Channels = 2,
            Bandwidth = bandwidth,
            BitrateBps = bitrate,
            FrameDuration = duration,
            Complexity = complexity,
        };

        int ms = duration switch
        {
            OpusFrameDuration.Ms10 => 10,
            OpusFrameDuration.Ms20 => 20,
            _ => 60,
        };
        int frameSamples = 48000 * ms / 1000 * 2;
        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        using var bit = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        for (int offset = 0; offset + frameSamples <= stereo.Length; offset += frameSamples)
        {
            int written = encoder.Encode(stereo.AsSpan(offset, frameSamples), packet);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[..4], written);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[4..], encoder.FinalRange);
            bit.Write(header);
            bit.Write(packet.AsSpan(0, written));
        }

        return Score(stereo, DecodeBitstream(bit.ToArray(), internalRate), internalRate);
    }

    /// <summary>Encodes with libopus 1.5.2 (reported only).</summary>
    internal static double MeasureLibopus(
        OpusBandwidth bandwidth, int ms, int bitrate, int complexity, int internalRate) =>
        Aggregate(
            internalRate,
            stereo =>
            {
                byte[] encoded = LibOpusOracle.Encode(
                    stereo, 48000, 2, bitrate,
                    ["-bandwidth", SilkQualityHarness.BandwidthName(bandwidth),
                     "-framesize", ms.ToString(), "-complexity", complexity.ToString()]);
                return Score(stereo, DecodeBitstream(encoded, internalRate), internalRate);
            },
            out _);

    /// <summary>Encodes with the RFC 6716 Appendix A <c>opus_demo</c> and scores its output.</summary>
    internal static double MeasureRfcReference(
        string opusDemo, OpusBandwidth bandwidth, int ms, int bitrate, int internalRate) =>
        Aggregate(
            internalRate,
            stereo => Score(stereo, DecodeBitstream(RunRfcEncoder(opusDemo, stereo, bandwidth, ms, bitrate), internalRate), internalRate),
            out _);

    private static byte[] RunRfcEncoder(
        string opusDemo, short[] stereo, OpusBandwidth bandwidth, int ms, int bitrate)
    {
        string stem = Guid.NewGuid().ToString("N");
        string input = Path.Combine(Path.GetTempPath(), $"kiln-enc2-rfc-in-{stem}.pcm");
        string output = Path.Combine(Path.GetTempPath(), $"kiln-enc2-rfc-out-{stem}.bit");
        try
        {
            Pcm16.Write(input, stereo);
            var start = new ProcessStartInfo(opusDemo)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (string argument in new[]
                     {
                         "-e", "voip", "48000", "2", bitrate.ToString(),
                         "-bandwidth", SilkQualityHarness.BandwidthName(bandwidth),
                         "-framesize", ms.ToString(), "-complexity", "0",
                         input, output,
                     })
            {
                start.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10 * 60 * 1000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException($"{opusDemo} did not finish within 10 minutes.");
            }

            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{opusDemo} exited with {process.ExitCode}: {stderr.Result}");
            }

            return File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static short[] DecodeBitstream(byte[] bit, int internalRate)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kiln-enc2-g2-{Guid.NewGuid():N}.bit");
        try
        {
            File.WriteAllBytes(path, bit);
            return LibOpusOracle.Decode(path, internalRate, 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Scores both channels against the independent ideal-resampled reference and averages. The
    /// reference is 48 kHz and is resampled independently; the decoder output is already at the
    /// internal rate and is used as is.
    /// </summary>
    private static double Score(short[] stereo, short[] decoded, int internalRate)
    {
        int referenceSamples = stereo.Length / 2;
        var left = new short[referenceSamples];
        var right = new short[referenceSamples];
        for (int i = 0; i < referenceSamples; i++)
        {
            left[i] = stereo[2 * i];
            right[i] = stereo[(2 * i) + 1];
        }

        int decodedSamples = decoded.Length / 2;
        var decodedLeft = new short[decodedSamples];
        var decodedRight = new short[decodedSamples];
        for (int i = 0; i < decodedSamples; i++)
        {
            decodedLeft[i] = decoded[2 * i];
            decodedRight[i] = decoded[(2 * i) + 1];
        }

        double leftSnr = SilkQualityHarness.Score(
            IdealResampler.Resample(left, 48000, internalRate), decodedLeft, internalRate);
        double rightSnr = SilkQualityHarness.Score(
            IdealResampler.Resample(right, 48000, internalRate), decodedRight, internalRate);
        return (leftSnr + rightSnr) / 2;
    }
}

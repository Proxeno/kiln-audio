using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using Kiln.Audio.Tests.Oracle;
using Kiln.Audio.Tests.SilkEncoder;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Hybrid G2 measurement: the same corpus, <see cref="IdealResampler"/> reference and
/// <see cref="SilkQualityHarness.Score"/> used for the SILK gate, with the hybrid bandwidths
/// (super-wideband and fullband) and the three encoders under comparison (ours, libopus 1.5.2,
/// and the RFC 6716 Appendix A reference build).
/// </summary>
internal static class HybridQualityHarness
{
    /// <summary>Scoring rate: hybrid output is fullband, so the reference needs no resampling.</summary>
    internal const int Rate = 48000;

    /// <summary>The opus_demo bandwidth name for a hybrid packet bandwidth.</summary>
    internal static string BandwidthName(OpusBandwidth bandwidth) => bandwidth switch
    {
        OpusBandwidth.SuperWideband => "SWB",
        OpusBandwidth.Fullband => "FB",
        _ => throw new ArgumentOutOfRangeException(nameof(bandwidth), bandwidth, "Hybrid frames are SWB or FB."),
    };

    /// <summary>Measures one encoder over the whole corpus; returns the segment-weighted mean segSNR.</summary>
    internal static double Aggregate(Func<short[], short[], double> measure, out string detail) =>
        SilkQualityHarness.Aggregate(Rate, measure, out detail);

    /// <summary>Encodes with our encoder, decodes with libopus and scores against the reference.</summary>
    internal static double MeasureOurs(
        short[] mono, OpusBandwidth bandwidth, int bitrate, int complexity, short[] reference)
    {
        var options = new OpusEncoderOptions
        {
            Application = OpusApplication.Voip,
            SampleRate = Rate,
            Channels = 1,
            Bandwidth = bandwidth,
            BitrateBps = bitrate,
            FrameDuration = OpusFrameDuration.Ms20,
            Complexity = complexity,
        };

        using var encoder = new OpusEncoder(options);
        var packet = new byte[1276];
        var bit = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        for (int offset = 0; offset + 960 <= mono.Length; offset += 960)
        {
            int written = encoder.Encode(mono.AsSpan(offset, 960), packet);
            BinaryPrimitives.WriteInt32BigEndian(header[..4], written);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], encoder.FinalRange);
            bit.Write(header);
            bit.Write(packet.AsSpan(0, written));
        }

        return SilkQualityHarness.Score(reference, SilkQualityHarness.DecodeBitstream(bit.ToArray(), Rate), Rate);
    }

    /// <summary>Encodes with libopus 1.5.2, decodes and scores.</summary>
    internal static double MeasureLibopus(
        short[] mono, OpusBandwidth bandwidth, int bitrate, int complexity, short[] reference)
    {
        byte[] encoded = LibOpusOracle.Encode(mono, Rate, 1, bitrate, Arguments(bandwidth, complexity));
        return SilkQualityHarness.Score(reference, SilkQualityHarness.DecodeBitstream(encoded, Rate), Rate);
    }

    /// <summary>Encodes with the RFC 6716 Appendix A build, decodes with libopus and scores.</summary>
    internal static double MeasureExternal(
        string opusDemo, short[] mono, OpusBandwidth bandwidth, int bitrate, int complexity, short[] reference)
    {
        string stem = Guid.NewGuid().ToString("N");
        string input = Path.Combine(Path.GetTempPath(), $"kiln-hybrid-rfc-in-{stem}.pcm");
        string output = Path.Combine(Path.GetTempPath(), $"kiln-hybrid-rfc-out-{stem}.bit");
        try
        {
            Pcm16.Write(input, mono);
            var start = new ProcessStartInfo(opusDemo)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (string argument in new[] { "-e", "voip", "48000", "1", bitrate.ToString(CultureInfo.InvariantCulture) })
            {
                start.ArgumentList.Add(argument);
            }

            foreach (string argument in Arguments(bandwidth, complexity))
            {
                start.ArgumentList.Add(argument);
            }

            start.ArgumentList.Add(input);
            start.ArgumentList.Add(output);
            using Process process = Process.Start(start)!;

            // Drain both pipes concurrently and bound the run, killing the whole tree on timeout.
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
                throw new InvalidOperationException($"{opusDemo} exited with {process.ExitCode}.");
            }

            return SilkQualityHarness.Score(
                reference, SilkQualityHarness.DecodeBitstream(File.ReadAllBytes(output), Rate), Rate);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    private static string[] Arguments(OpusBandwidth bandwidth, int complexity) =>
    [
        "-bandwidth", BandwidthName(bandwidth),
        "-framesize", "20",
        "-complexity", complexity.ToString(CultureInfo.InvariantCulture),
    ];
}

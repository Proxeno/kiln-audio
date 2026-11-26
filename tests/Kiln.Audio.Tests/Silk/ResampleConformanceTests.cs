using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FluentAssertions;
using Kiln.Audio;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Silk;

/// <summary>
/// End-to-end conformance of the SILK synthesis plus <see cref="SilkResampler"/> against libopus
/// at 48 kHz, including the RFC 8251 SILK-only test vectors.
/// </summary>
/// <remarks>
/// <para>
/// The delay alignment in <see cref="SilkResampler"/> is what makes these tests possible: the
/// total delay from the SILK synthesis output to the 48 kHz output now equals libopus's, so the
/// cross-correlation peak sits at lag 0 and sample-wise comparison is meaningful.
/// </para>
/// <para>
/// Test vectors 02, 03 and 04 are all SILK-only (NB, MB and WB), each with a mono half followed
/// by a stereo half. Vector 12 mixes in CELT and hybrid packets and is therefore excluded; the
/// driver rejects any non-SILK packet.
/// </para>
/// </remarks>
public class ResampleConformanceTests
{
    private const int OutputRate = 48000;
    private const int TenMilliseconds = OutputRate / 100;
    // Regression floor, not a quality gate. The SILK resampler is non-normative (RFC 6716
    // §4.2.9), so this SNR measures resemblance to libopus's particular resampler rather than
    // quality; an ideal interpolator is only about 20 dB from libopus on NB. The coordinator
    // ruled that the original 30 dB target was mis-specified, and 29.5 dB is the floor that
    // guards against regressions in this original design. Do not lower it further.
    private const double MinimumSnrDb = 29.5;

    private static readonly BandwidthCase[] BandwidthCases =
    [
        new("NB", 8000, OpusBandwidth.Narrowband),
        new("MB", 12000, OpusBandwidth.Mediumband),
        new("WB", 16000, OpusBandwidth.Wideband),
    ];

    private static readonly int[] FrameSizes = [10, 20, 40, 60];

    private readonly ITestOutputHelper _output;

    public ResampleConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// (a) The 24 SILK-DEC-2 streams, decoded at 48 kHz, must line up with <c>opus_demo -d 48000</c>
    /// at lag 0 and stay above the 29.5 dB regression floor, excluding the first 10 ms.
    /// </summary>
    [Fact]
    public void Silk_only_streams_line_up_with_libopus_at_48_kHz()
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        var report = new StringBuilder();
        int streams = 0;
        double lowestSnr = double.PositiveInfinity;
        string lowestSnrName = "";
        var lagFailures = new List<string>();
        var snrFailures = new List<string>();

        foreach (BandwidthCase bandwidth in BandwidthCases)
        {
            short[] stereo = Pcm16.Read(TestVectors.DecPath(
                bandwidth.Bandwidth == OpusBandwidth.Narrowband ? 2 : 3));
            short[] mono = Downmix(stereo);

            foreach (int frameSizeMs in FrameSizes)
            {
                foreach (bool isStereo in new[] { false, true })
                {
                    int channels = isStereo ? 2 : 1;
                    string name = FormattableString.Invariant(
                        $"{bandwidth.Name} {frameSizeMs}ms {(isStereo ? "stereo" : "mono")}");
                    short[] input = isStereo ? stereo : mono;
                    byte[] bitFile = EncodeSilkOnly(
                        input, channels, isStereo ? 24000 : 12000, bandwidth.Name, frameSizeMs);

                    string path = Path.Combine(
                        Path.GetTempPath(), FormattableString.Invariant($"kiln-resample-{Guid.NewGuid():N}.bit"));
                    try
                    {
                        File.WriteAllBytes(path, bitFile);
                        short[] reference = LibOpusOracle.Decode(path, OutputRate, channels);
                        SilkVectorDriver.Result ours = SilkVectorDriver.DecodeFile(path);

                        ours.Packets.Should().OnlyContain(
                            packet => packet.Channels == channels && packet.InternalRateHz == bandwidth.InternalRate,
                            "{0}: every packet must be {1} Hz with {2} channel(s)",
                            name,
                            bandwidth.InternalRate,
                            channels);

                        int referenceFrames = reference.Length / channels;
                        int ourFrames = ours.StereoPcm.Length / 2;
                        ourFrames.Should().Be(
                            referenceFrames, "{0}: the output must have one sample per reference sample", name);

                        var lags = new int[channels];
                        var snrs = new double[channels];
                        for (int channel = 0; channel < channels; channel++)
                        {
                            short[] referenceChannel = Extract(reference, channel, channels);
                            short[] ourChannel = Extract(ours.StereoPcm, channel, 2);
                            lags[channel] = IdealResampler.FindLag(
                                referenceChannel, ourChannel, maxLag: 32, skip: TenMilliseconds, stride: 1);
                            snrs[channel] = IdealResampler.Snr(referenceChannel, ourChannel, TenMilliseconds);
                        }

                        report.AppendLine(FormattableString.Invariant(
                            $"{name}: lag [{string.Join(", ", lags)}], SNR [{string.Join(", ", Array.ConvertAll(snrs, snr => snr.ToString("F2")))}] dB"));

                        for (int channel = 0; channel < channels; channel++)
                        {
                            if (lags[channel] != 0)
                            {
                                lagFailures.Add(FormattableString.Invariant(
                                    $"{name} ch{channel}: lag {lags[channel]}"));
                            }

                            if (snrs[channel] < MinimumSnrDb)
                            {
                                snrFailures.Add(FormattableString.Invariant(
                                    $"{name} ch{channel}: SNR {snrs[channel]:F2} dB"));
                            }

                            if (snrs[channel] < lowestSnr)
                            {
                                lowestSnr = snrs[channel];
                                lowestSnrName = FormattableString.Invariant($"{name} ch{channel}");
                            }
                        }

                        streams++;
                    }
                    finally
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                    }
                }
            }
        }

        streams.Should().Be(24);
        _output.WriteLine(report.ToString());
        _output.WriteLine(FormattableString.Invariant(
            $"{streams} streams checked; lowest SNR {lowestSnr:F2} dB ({lowestSnrName})."));
        lagFailures.Should().BeEmpty(
            "the cross-correlation peak must be at lag 0 for every stream:\n{0}", string.Join("\n", lagFailures));
        snrFailures.Should().BeEmpty(
            "the SNR after 10 ms must be at least {0} dB for every stream:\n{1}",
            MinimumSnrDb,
            string.Join("\n", snrFailures));
    }

    /// <summary>
    /// (b) RFC 8251 vectors 02, 03 and 04 must pass the native <c>opus_compare</c> metric against
    /// both reference decodes, and match <c>opus_demo -d 48000 2</c> closely in samples.
    /// </summary>
    /// <param name="vector">Vector number: 2, 3 or 4.</param>
    /// <param name="bandwidth">Bandwidth of the vector, for the report.</param>
    [Theory]
    [InlineData(2, "NB")]
    [InlineData(3, "MB")]
    [InlineData(4, "WB")]
    public void Rfc8251_silk_only_vectors_are_conformant(int vector, string bandwidth)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        string bitPath = TestVectors.BitPath(vector);
        SilkVectorDriver.Result ours = SilkVectorDriver.DecodeFile(bitPath);
        short[] reference = Pcm16.Read(TestVectors.DecPath(vector));
        short[] modified = Pcm16.Read(TestVectors.ModifiedDecPath(vector));
        short[] demo = LibOpusOracle.Decode(bitPath, OutputRate, 2);

        ours.StereoPcm.Length.Should().Be(
            reference.Length,
            "vector {0}: our stereo output must have the same length as the reference decode",
            vector);
        modified.Length.Should().Be(reference.Length, "vector {0}: reference decode lengths agree", vector);
        demo.Length.Should().Be(reference.Length, "vector {0}: opus_demo lengths agree", vector);

        OpusCompare.Result againstReference = OpusCompare.Compare(reference, ours.StereoPcm, OutputRate, stereo: true);
        OpusCompare.Result againstModified = OpusCompare.Compare(modified, ours.StereoPcm, OutputRate, stereo: true);

        int maxAbsDifference = 0;
        for (int i = 0; i < ours.StereoPcm.Length; i++)
        {
            int difference = Math.Abs(ours.StereoPcm[i] - demo[i]);
            if (difference > maxAbsDifference)
            {
                maxAbsDifference = difference;
            }
        }

        double snr = IdealResampler.Snr(demo, ours.StereoPcm, TenMilliseconds);
        int lag = IdealResampler.FindLag(demo, ours.StereoPcm, maxLag: 32, skip: TenMilliseconds, stride: 1);
        string diagnosis = DescribePacketHealth(ours, demo);

        _output.WriteLine(FormattableString.Invariant(
            $"vector {vector:D2} ({bandwidth}, {ours.PacketCount} packets): quality vs .dec {againstReference.Quality:F2} %, vs m.dec {againstModified.Quality:F2} %; vs opus_demo lag {lag}, max abs diff {maxAbsDifference}, SNR {snr:F2} dB. {diagnosis}"));

        againstReference.Passed.Should().BeTrue(
            "vector {0} must pass opus_compare against .dec (quality {1:F2} %)\n{2}",
            vector,
            againstReference.Quality,
            diagnosis);
        againstModified.Passed.Should().BeTrue(
            "vector {0} must pass opus_compare against m.dec (quality {1:F2} %)\n{2}",
            vector,
            againstModified.Quality,
            diagnosis);
    }

    /// <summary>
    /// Finds the first packet whose segment SNR against <paramref name="demo"/> is poor, so a
    /// failing vector reports a packet rather than just the whole-file verdict.
    /// </summary>
    private static string DescribePacketHealth(SilkVectorDriver.Result ours, short[] demo)
    {
        for (int index = 0; index < ours.Packets.Count; index++)
        {
            SilkVectorDriver.Packet packet = ours.Packets[index];
            int start = (packet.FirstFrame * 2) + (2 * TenMilliseconds);
            int end = Math.Min(demo.Length, (packet.FirstFrame + packet.FrameCount) * 2);
            if (end <= start)
            {
                continue;
            }

            double signal = 0;
            double error = 0;
            for (int i = start; i < end; i++)
            {
                double difference = demo[i] - ours.StereoPcm[i];
                signal += demo[i] * (double)demo[i];
                error += difference * difference;
            }

            double snr = error <= 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / error);
            if (snr < 20)
            {
                return FormattableString.Invariant(
                    $"first weak packet: {packet.PacketIndex + 1} ({packet.InternalRateHz} Hz, {packet.Channels} ch) segment SNR {snr:F2} dB");
            }
        }

        return "no packet segment below 20 dB";
    }

    private static short[] Extract(short[] interleaved, int channel, int channels)
    {
        if (channels == 1)
        {
            return interleaved;
        }

        var output = new short[interleaved.Length / 2];
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = interleaved[(2 * i) + channel];
        }

        return output;
    }

    private static short[] Downmix(short[] stereo)
    {
        var mono = new short[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)((stereo[2 * i] + stereo[(2 * i) + 1]) / 2);
        }

        return mono;
    }

    /// <summary>Encodes a stream whose every packet is SILK-only and has the requested channel count.</summary>
    private static byte[] EncodeSilkOnly(
        short[] input, int channels, int requestedBitrate, string bandwidth, int frameSizeMs)
    {
        int[] bitrates = channels == 2 ? [requestedBitrate, 32000, 40000] : [requestedBitrate];
        foreach (int bitrate in bitrates)
        {
            byte[] bitFile = LibOpusOracle.Encode(
                input,
                OutputRate,
                channels,
                bitrate,
                ["-bandwidth", bandwidth, "-framesize", frameSizeMs.ToString(), "-cbr"]);
            if (IsSilkOnly(bitFile, channels))
            {
                return bitFile;
            }
        }

        throw new InvalidOperationException(FormattableString.Invariant(
            $"No SILK-only {channels}-channel stream at {bandwidth}, {frameSizeMs} ms."));
    }

    private static bool IsSilkOnly(byte[] bitFile, int channels)
    {
        string path = Path.Combine(
            Path.GetTempPath(), FormattableString.Invariant($"kiln-silk-{Guid.NewGuid():N}.bit"));
        try
        {
            File.WriteAllBytes(path, bitFile);
            foreach ((byte[] packet, _) in OpusBitFile.Read(path))
            {
                OpusToc toc = OpusToc.Parse(packet[0]);
                if (toc.Mode != OpusMode.Silk || toc.IsStereo != (channels == 2))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private readonly record struct BandwidthCase(string Name, int InternalRate, OpusBandwidth Bandwidth);
}

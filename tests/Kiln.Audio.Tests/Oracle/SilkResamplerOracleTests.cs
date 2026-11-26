using System;
using System.Collections.Concurrent;
using FluentAssertions;
using Kiln.Audio.Silk;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.Oracle;

/// <summary>
/// Compares the managed SILK resampler with the pinned libopus decoder on SILK-only streams.
/// </summary>
/// <remarks>
/// <para>
/// Each stream is encoded from a mono downmix of <c>testvector02.dec</c> with <c>opus_demo -e</c>
/// and decoded twice: once at the SILK internal rate by libopus itself, and once at 48 kHz. The
/// internal decode is the input to <see cref="SilkResampler"/>; the task is to reach the 48 kHz
/// decode as closely as possible.
/// </para>
/// <para>
/// libopus's internal-rate decode is not free of resampling: its decoder resampler runs in copy
/// mode and delays the signal by a fixed number of samples relative to the internal SILK
/// synthesis. That internal signal is not exposed by any public API, so the copy delay cannot be
/// observed by cross-correlation. What is observable is the delay of the 48 kHz decode relative
/// to a zero-delay band-limited interpolation of the internal decode, and that is what the tests
/// measure and report.
/// </para>
/// </remarks>
public class SilkResamplerOracleTests
{
    private const int OutRate = 48000;

    private static readonly ConcurrentDictionary<int, Lazy<short[]>> Sources = new();
    private static readonly ConcurrentDictionary<(int Rate, string Bandwidth, int Bitrate), Lazy<Stream>> Streams = new();

    private readonly ITestOutputHelper _output;

    public SilkResamplerOracleTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<int, string, int> Cases()
    {
        var data = new TheoryData<int, string, int>();
        foreach ((int rate, string bandwidth) in new[] { (8000, "NB"), (12000, "MB"), (16000, "WB") })
        {
            data.Add(rate, bandwidth, 12000);
            data.Add(rate, bandwidth, 20000);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Resampler_quality_matches_the_reference_limit(int inRate, string bandwidth, int bitrate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        Stream stream = GetStream(inRate, bandwidth, bitrate);
        var resampler = new SilkResampler(inRate, OutRate);
        var ours = new short[resampler.OutputCount(stream.Internal.Length)];
        resampler.Process(stream.Internal, ours);

        short[] ideal = IdealResampler.ToPcm(IdealResampler.Resample(stream.Internal, inRate, OutRate));
        int referenceLag = IdealResampler.FindLag(ideal, stream.Out48, maxLag: 64, skip: OutRate / 10, stride: 2);
        int ourLag = IdealResampler.FindLag(ideal, ours, maxLag: 64, skip: OutRate / 10, stride: 2);

        int alignment = referenceLag - ourLag;
        short[] aligned = IdealResampler.Shift(ours, alignment);
        short[] alignedIdeal = IdealResampler.Shift(ideal, referenceLag);

        double snrOurs = IdealResampler.Snr(stream.Out48, aligned, OutRate / 10);
        double snrIdeal = IdealResampler.Snr(stream.Out48, alignedIdeal, OutRate / 10);

        var reference = new short[stream.Out48.Length * 2];
        for (int i = 0; i < stream.Out48.Length; i++)
        {
            reference[2 * i] = stream.Out48[i];
            reference[(2 * i) + 1] = stream.Out48[i];
        }

        LibOpusOracle.Comparison qualityOurs = LibOpusOracle.Compare(reference, aligned, OutRate, stereo: false);
        LibOpusOracle.Comparison qualityIdeal = LibOpusOracle.Compare(reference, alignedIdeal, OutRate, stereo: false);

        _output.WriteLine(FormattableString.Invariant(
            $"{inRate}->{OutRate} {bandwidth} {bitrate} bps: libopus lag {referenceLag}, ours {ourLag} (documented {resampler.DelaySamplesOut}), SNR ours {snrOurs:F2} dB vs ideal {snrIdeal:F2} dB, quality ours {qualityOurs.Quality:F1} % vs ideal {qualityIdeal.Quality:F1} %"));

        // The minimum-phase prototype's delay is frequency dependent, so the cross-correlation
        // delay of a speech signal can differ from the broadband nominal by a few samples.
        ourLag.Should().BeInRange(
            resampler.DelaySamplesOut - 4,
            resampler.DelaySamplesOut + 4,
            "the cross-correlation delay must match the documented latency");
        snrOurs.Should().BeGreaterThanOrEqualTo(
            30.0,
            "the resampler output must stay within 30 dB of libopus's 48 kHz decode");
        snrOurs.Should().BeGreaterThanOrEqualTo(
            snrIdeal - 8.0,
            "the designed filter must stay close to an ideal interpolator");
        qualityOurs.Quality.Should().BeGreaterThanOrEqualTo(
            qualityIdeal.Quality - 5.0,
            "the designed filter must score close to an ideal interpolator");
        qualityOurs.Passed.Should().BeTrue("the native opus_compare metric must accept the output");
    }

    private static Stream GetStream(int inRate, string bandwidth, int bitrate) =>
        Streams.GetOrAdd(
            (inRate, bandwidth, bitrate),
            key => new Lazy<Stream>(() => BuildStream(key.Rate, key.Bandwidth, key.Bitrate))).Value;

    private static Stream BuildStream(int inRate, string bandwidth, int bitrate)
    {
        short[] source = Sources.GetOrAdd(
            inRate,
            rate => new Lazy<short[]>(() => BuildSource(rate))).Value;

        byte[] packets = LibOpusOracle.Encode(
            source, inRate, 1, bitrate, ["-bandwidth", bandwidth, "-framesize", "20"]);
        string bitPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), FormattableString.Invariant($"kiln-silk-{Guid.NewGuid():N}.bit"));
        try
        {
            System.IO.File.WriteAllBytes(bitPath, packets);
            short[] internalPcm = LibOpusOracle.Decode(bitPath, inRate, 1);
            short[] out48 = LibOpusOracle.Decode(bitPath, OutRate, 1);
            return new Stream(internalPcm, out48);
        }
        finally
        {
            if (System.IO.File.Exists(bitPath))
            {
                System.IO.File.Delete(bitPath);
            }
        }
    }

    private static short[] BuildSource(int rate)
    {
        short[] stereo = Pcm16.Read(TestVectors.DecPath(2));
        var mono = new short[stereo.Length / 2];
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] = (short)Math.Clamp(
                (int)Math.Round(0.5 * (stereo[2 * i] + stereo[(2 * i) + 1])),
                short.MinValue,
                short.MaxValue);
        }

        return IdealResampler.ToPcm(IdealResampler.Resample(mono, 48000, rate));
    }

    private sealed record Stream(short[] Internal, short[] Out48);
}

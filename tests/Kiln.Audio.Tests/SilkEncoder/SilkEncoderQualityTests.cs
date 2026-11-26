using FluentAssertions;
using Kiln.Audio.Tests.Oracle;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// G2: aggregate segmental SNR (segSNR) of our SILK encoder.
/// </summary>
/// <remarks>
/// <para>
/// Both encoders are scored against the same independent reference: the 48 kHz corpus passed through
/// <see cref="IdealResampler"/>, a long windowed sinc that shares no code with our encoder's input
/// resampler. (An earlier version built the reference with our own resampler, which biased the
/// comparison.)
/// </para>
/// <para>
/// HARD gate: at complexity 0 our aggregate segSNR must be at least the RFC 6716 Appendix A
/// reference encoder's, less <see cref="SilkRfcBaseline.ToleranceDb"/> dB of measurement noise. The
/// RFC figures are frozen in <see cref="SilkRfcBaseline"/>. The gap to libopus 1.5.2 at complexity 0,
/// 5 and 10 is reported, not asserted.
/// </para>
/// </remarks>
public class SilkEncoderQualityTests
{
    private readonly ITestOutputHelper _output;

    public SilkEncoderQualityTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<OpusBandwidth, OpusFrameDuration, int, int> Cases()
    {
        var data = new TheoryData<OpusBandwidth, OpusFrameDuration, int, int>();
        foreach (OpusBandwidth bandwidth in new[]
                 {
                     OpusBandwidth.Narrowband, OpusBandwidth.Mediumband, OpusBandwidth.Wideband,
                 })
        {
            foreach (OpusFrameDuration duration in new[] { OpusFrameDuration.Ms20, OpusFrameDuration.Ms60 })
            {
                foreach (int bitrate in new[] { 8000, 12000, 20000 })
                {
                    if (bandwidth == OpusBandwidth.Narrowband && bitrate == 20000)
                    {
                        continue;
                    }

                    foreach (int complexity in new[] { 0, 5, 10 })
                    {
                        data.Add(bandwidth, duration, bitrate, complexity);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Quality_against_rfc_baseline_and_libopus(
        OpusBandwidth bandwidth, OpusFrameDuration duration, int bitrate, int complexity)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        int ms = SilkQualityHarness.Milliseconds(duration);
        int internalRate = SilkQualityHarness.InternalRate(bandwidth);

        double ours = SilkQualityHarness.Aggregate(
            internalRate,
            (mono, reference) => SilkQualityHarness.MeasureOurs(
                mono, bandwidth, duration, bitrate, complexity, internalRate, reference),
            out string oursDetail);
        double libopus = SilkQualityHarness.Aggregate(
            internalRate,
            (mono, reference) => SilkQualityHarness.MeasureLibopus(
                mono, bandwidth, ms, bitrate, complexity, internalRate, reference),
            out string libopusDetail);

        bool hasRfc = SilkRfcBaseline.TryGet(bandwidth, ms, bitrate, out double rfcValue);
        string rfcText = complexity == 0 ? rfcValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "n/a";
        _output.WriteLine(
            $"SILK {bandwidth} {ms}ms {bitrate}bps c{complexity}: ours {ours:F2} dB, RFC {rfcText} dB, "
            + $"libopus 1.5.2 {libopus:F2} dB, gap to libopus {ours - libopus:F2} dB");
        _output.WriteLine($"  ours per vector: {oursDetail}");
        _output.WriteLine($"  libopus per vector: {libopusDetail}");

        if (complexity == 0)
        {
            hasRfc.Should().BeTrue($"the frozen RFC baseline must cover {bandwidth} {ms} ms {bitrate} bps");
            ours.Should().BeGreaterThanOrEqualTo(
                rfcValue - SilkRfcBaseline.ToleranceDb,
                $"G2: ours must be >= the RFC 6716 Appendix A encoder ({bandwidth}, {ms} ms, {bitrate} bps): "
                + $"ours {ours:F2} dB, RFC {rfcValue:F2} dB");
        }
    }
}

using System.Globalization;
using FluentAssertions;
using Kiln.Audio.Tests.Oracle;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Hybrid G2: aggregate segmental SNR at complexity 0 against the frozen RFC 6716 Appendix A
/// baseline. The libopus 1.5.2 gap is reported, never gated.
/// </summary>
public class HybridEncoderQualityTests
{
    public static TheoryData<OpusBandwidth, int> Cases()
    {
        var data = new TheoryData<OpusBandwidth, int>();
        foreach (OpusBandwidth bandwidth in new[] { OpusBandwidth.SuperWideband, OpusBandwidth.Fullband })
        {
            foreach (int bitrate in new[] { 24000, 32000 })
            {
                data.Add(bandwidth, bitrate);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Hybrid_quality_reaches_the_rfc_baseline(OpusBandwidth bandwidth, int bitrate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        double ours = HybridQualityHarness.Aggregate(
            (mono, reference) => HybridQualityHarness.MeasureOurs(mono, bandwidth, bitrate, 0, reference),
            out string oursDetail);
        double libopus = HybridQualityHarness.Aggregate(
            (mono, reference) => HybridQualityHarness.MeasureLibopus(mono, bandwidth, bitrate, 0, reference),
            out string libopusDetail);

        HybridRfcBaseline.TryGet(bandwidth, bitrate, out double baseline).Should().BeTrue(
            "every hybrid G2 configuration must have a frozen RFC baseline");

        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"hybrid {bandwidth} {bitrate} c0: ours {ours:F2} dB, RFC {baseline:F2} dB, libopus {libopus:F2} dB " +
            $"(gap vs libopus {ours - libopus:+0.000;-0.000} dB); ours [{oursDetail}]; libopus [{libopusDetail}]");
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "kiln-hybrid-g2-report.txt"), report + Environment.NewLine);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "kiln-hybrid-g2-report.txt"), report + Environment.NewLine);

        ours.Should().BeGreaterThanOrEqualTo(
            baseline - HybridRfcBaseline.ToleranceDb,
            $"hybrid {bandwidth} {bitrate} c0 must stay within {HybridRfcBaseline.ToleranceDb} dB of the RFC encoder; {report}");
    }
}

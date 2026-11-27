using System.Globalization;
using FluentAssertions;
using Kiln.Audio.Tests.Oracle;
using Kiln.Audio.Tests.SilkEncoder;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Full-band hybrid gate (added for 1.0): the generated <see cref="FullbandCorpus"/> item carries
/// content from 13 to 19 kHz, so it can tell a fullband stream that codes that band from a
/// super-wideband one that does not. <see cref="HybridEncoderQualityTests"/> is unchanged.
/// </summary>
public class HybridFullbandQualityTests
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

    [Fact]
    public void Corpus_is_deterministic_and_has_content_above_12_kHz()
    {
        short[] mono = FullbandCorpus.Mono;
        mono.Length.Should().Be(288000);

        double share =
            FullbandCorpus.BandEnergyDb(mono, FullbandCorpus.HighBandLowHz, FullbandCorpus.HighBandHighHz, 0)
            - FullbandCorpus.BandEnergyDb(mono, 0, 20000, 0);
        share.Should().BeGreaterThanOrEqualTo(HybridFullbandBaseline.MinHighBandShareDb);

        int peak = 0;
        foreach (short sample in mono)
        {
            peak = Math.Max(peak, Math.Abs((int)sample));
        }

        peak.Should().BeGreaterThan(8000);
        peak.Should().BeLessThan(32000, "the item must not clip");

        FullbandCorpus.Build().Should().Equal(mono, "the corpus is generated deterministically");
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(64000)]
    public void Fullband_keeps_the_band_above_12_kHz_and_super_wideband_does_not(int bitrate)
    {
        (byte[] fbBits, int[] fbModes) = HybridFullbandHarness.EncodeOurs(OpusBandwidth.Fullband, bitrate, 0);
        (byte[] swbBits, int[] swbModes) = HybridFullbandHarness.EncodeOurs(OpusBandwidth.SuperWideband, bitrate, 0);
        double fbRetention = HybridFullbandHarness.HighBandRetentionDb(HybridFullbandHarness.DecodeOurs(fbBits));
        double swbRetention = HybridFullbandHarness.HighBandRetentionDb(HybridFullbandHarness.DecodeOurs(swbBits));

        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"retention {bitrate} c0: FB {fbRetention:F2} dB, SWB {swbRetention:F2} dB, " +
            $"FB modes [silk {fbModes[0]}, hybrid {fbModes[1]}, celt {fbModes[2]}], " +
            $"SWB modes [silk {swbModes[0]}, hybrid {swbModes[1]}, celt {swbModes[2]}]");
        HybridFullbandHarness.Report(report);

        fbRetention.Should().BeGreaterThanOrEqualTo(HybridFullbandBaseline.MinFullbandRetentionDb, report);
        (fbRetention - swbRetention).Should().BeGreaterThanOrEqualTo(
            HybridFullbandBaseline.MinFullbandOverSuperWidebandDb, report);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Hybrid_quality_on_the_fullband_item_reaches_the_rfc_baseline(OpusBandwidth bandwidth, int bitrate)
    {
        if (!OracleTestSupport.OracleAvailable())
        {
            return;
        }

        short[] mono = FullbandCorpus.Mono;
        short[] reference = HybridFullbandHarness.Reference;
        double ours = HybridQualityHarness.MeasureOurs(mono, bandwidth, bitrate, 0, reference);
        double libopus = HybridQualityHarness.MeasureLibopus(mono, bandwidth, bitrate, 0, reference);

        HybridFullbandBaseline.TryGet(bandwidth, bitrate, out double baseline).Should().BeTrue(
            "every full-band configuration must have a frozen RFC baseline");

        string[] arguments =
        [
            "-bandwidth", HybridQualityHarness.BandwidthName(bandwidth),
            "-framesize", "20",
            "-complexity", "0",
        ];
        byte[] encoded = LibOpusOracle.Encode(mono, FullbandCorpus.Rate, 1, bitrate, arguments);
        double libopusRetention = HybridFullbandHarness.HighBandRetentionDb(
            SilkQualityHarness.DecodeBitstream(encoded, FullbandCorpus.Rate));

        string report = string.Create(
            CultureInfo.InvariantCulture,
            $"fullband item {bandwidth} {bitrate} c0: ours {ours:F2} dB, RFC {baseline:F2} dB, libopus {libopus:F2} dB, " +
            $"libopus 13-19 kHz retention {libopusRetention:F2} dB");
        HybridFullbandHarness.Report(report);

        if (!HybridFullbandBaseline.IsGated(bandwidth))
        {
            // Super-wideband rows are measured and reported only; see HybridFullbandBaseline.
            return;
        }

        ours.Should().BeGreaterThanOrEqualTo(
            baseline - HybridFullbandBaseline.ToleranceDb,
            $"hybrid {bandwidth} {bitrate} c0 on the full-band item must stay within {HybridFullbandBaseline.ToleranceDb} dB of the RFC encoder; {report}");
    }
}

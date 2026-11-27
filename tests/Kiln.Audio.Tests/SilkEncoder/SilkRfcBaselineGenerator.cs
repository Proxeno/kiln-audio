using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Regenerates <see cref="SilkRfcBaseline"/>. Needs an RFC 6716 Appendix A <c>opus_demo</c> built
/// with FIXED_POINT=1; set <c>KILN_SILK_RFC_OPUS_DEMO</c> to its path. Without it the test passes
/// without doing anything, which is the case in CI.
/// </summary>
public class SilkRfcBaselineGenerator
{
    private readonly ITestOutputHelper _output;

    public SilkRfcBaselineGenerator(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Generate_baseline_table_when_the_rfc_encoder_is_available()
    {
        string? exe = Environment.GetEnvironmentVariable("KILN_SILK_RFC_OPUS_DEMO");
        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        var table = new StringBuilder();
        foreach (OpusBandwidth bandwidth in new[] { OpusBandwidth.Narrowband, OpusBandwidth.Mediumband, OpusBandwidth.Wideband })
        {
            int rate = SilkQualityHarness.InternalRate(bandwidth);
            foreach (int ms in new[] { 20, 60 })
            {
                foreach (int bitrate in new[] { 8000, 12000, 20000 })
                {
                    if (bandwidth == OpusBandwidth.Narrowband && bitrate == 20000)
                    {
                        continue;
                    }

                    double db = SilkQualityHarness.Aggregate(
                        rate,
                        (mono, reference) => SilkQualityHarness.MeasureExternal(
                            exe, mono, bandwidth, ms, bitrate, 0, rate, reference),
                        out _);
                    table.AppendLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"        [(OpusBandwidth.{bandwidth}, {ms}, {bitrate})] = {db:F2},"));
                }
            }
        }

        _output.WriteLine(table.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "kiln-silk-rfc-baseline.txt"), table.ToString());
    }
}

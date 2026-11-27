using System.Globalization;
using System.Text;
using Xunit.Abstractions;

namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Regenerates <see cref="HybridRfcBaseline"/>. Needs an RFC 6716 Appendix A <c>opus_demo</c>
/// built with FIXED_POINT=1; set <c>KILN_HYBRID_RFC_OPUS_DEMO</c> to its path. Without it the test
/// passes without doing anything, which is the case in CI.
/// </summary>
public class HybridRfcBaselineGenerator
{
    private readonly ITestOutputHelper _output;

    public HybridRfcBaselineGenerator(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Generate_baseline_table_when_the_rfc_encoder_is_available()
    {
        string? exe = Environment.GetEnvironmentVariable("KILN_HYBRID_RFC_OPUS_DEMO");
        if (string.IsNullOrEmpty(exe))
        {
            // Fallback for runners that do not pass the environment through to the test host:
            // look next to the test binary first, then in its working directory.
            foreach (string candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, "kiln-hybrid-rfc-demo.txt"),
                         Path.Combine(Environment.CurrentDirectory, "kiln-hybrid-rfc-demo.txt"),
                         Path.Combine(Path.GetTempPath(), "kiln-hybrid-rfc-demo.txt"),
                     })
            {
                if (File.Exists(candidate))
                {
                    exe = File.ReadAllText(candidate).Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(exe))
        {
            return;
        }

        var table = new StringBuilder();
        foreach (OpusBandwidth bandwidth in new[] { OpusBandwidth.SuperWideband, OpusBandwidth.Fullband })
        {
            foreach (int bitrate in new[] { 24000, 32000 })
            {
                double db = HybridQualityHarness.Aggregate(
                    (mono, reference) => HybridQualityHarness.MeasureExternal(
                        exe, mono, bandwidth, bitrate, 0, reference),
                    out string detail);
                table.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"        [(OpusBandwidth.{bandwidth}, {bitrate})] = {db:F2},"));
                _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{bandwidth} {bitrate}: {db:F2} ({detail})"));
            }
        }

        _output.WriteLine(table.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "kiln-hybrid-rfc-baseline.txt"), table.ToString());
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "kiln-hybrid-rfc-baseline.txt"), table.ToString());
    }
}

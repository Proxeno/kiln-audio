namespace Kiln.Audio.Tests.OpusEncoderTop;

/// <summary>
/// Frozen hybrid G2 baseline: aggregate segmental SNR of the RFC 6716 Appendix A reference encoder.
/// </summary>
/// <remarks>
/// <para>
/// Produced with the same corpus (RFC 8251 vectors 2, 3, 4 and 6, mixed to mono, 48 kHz), the same
/// <see cref="HybridQualityHarness"/>, the RFC build's own <c>opus_demo</c> and the libopus 1.5.2
/// decoder, and the independent <c>IdealResampler</c> reference. Each configuration is
/// <c>-e voip 48000 1 BITRATE -bandwidth {SWB|FB} -framesize 20 -complexity 0</c>. The figure is
/// the segment-weighted mean segSNR in dB. Regenerate with <see cref="HybridRfcBaselineGenerator"/>
/// (set <c>KILN_HYBRID_RFC_OPUS_DEMO</c>).
/// </para>
/// <para>
/// The hard gate asserts ours &gt;= baseline - <see cref="ToleranceDb"/> dB. The libopus 1.5.2 gap
/// is reported separately; it is not part of this gate.
/// </para>
/// </remarks>
internal static class HybridRfcBaseline
{
    /// <summary>Allowed shortfall in dB, a measurement-noise allowance.</summary>
    internal const double ToleranceDb = 0.05;

    private static readonly Dictionary<(OpusBandwidth, int), double> Table = new()
    {
        [(OpusBandwidth.SuperWideband, 24000)] = 5.01,
        [(OpusBandwidth.SuperWideband, 32000)] = 5.76,
        [(OpusBandwidth.Fullband, 24000)] = 4.97,
        [(OpusBandwidth.Fullband, 32000)] = 5.56,
    };

    /// <summary>Looks up the frozen RFC figure for a hybrid configuration at complexity 0.</summary>
    internal static bool TryGet(OpusBandwidth bandwidth, int bitrate, out double db) =>
        Table.TryGetValue((bandwidth, bitrate), out db);
}

namespace Kiln.Audio.Tests.SilkEncoder;

/// <summary>
/// Frozen G2 baseline: aggregate segmental SNR of the RFC 6716 Appendix A reference encoder on the
/// stereo corpus.
/// </summary>
/// <remarks>
/// <para>
/// How these numbers were produced (2026-11-27): the RFC 6716 Appendix A reference source, built
/// with FIXED_POINT=1 and its own <c>opus_demo</c>, encoded the stereo G2 corpus (the mono G2
/// corpus panned to left = signal, right = 0.6 signal) as
/// <c>-e voip 48000 2 BITRATE -bandwidth BW -framesize MS -complexity 0</c>. The packets were
/// decoded with the libopus 1.5.2 oracle decoder and scored with the same segmental SNR and the
/// same independent <see cref="IdealResampler"/> reference as our encoder.
/// </para>
/// <para>
/// The stereo G2 gate asserts ours &gt;= baseline - <see cref="ToleranceDb"/> dB. The gap to
/// libopus 1.5.2 is reported, not asserted. Regenerate with <see cref="Enc2StereoRfcBaselineGenerator"/>
/// (set <c>KILN_SILK_RFC_OPUS_DEMO</c>).
/// </para>
/// </remarks>
internal static class Enc2StereoRfcBaseline
{
    /// <summary>Allowed shortfall in dB, a measurement-noise allowance.</summary>
    internal const double ToleranceDb = 0.05;

    private static readonly Dictionary<(OpusBandwidth, int, int), double> Table = new()
    {
        [(OpusBandwidth.Wideband, 20, 24000)] = 4.87,
        [(OpusBandwidth.Wideband, 60, 24000)] = 4.92,
    };

    /// <summary>Looks up the frozen RFC figure for a stereo configuration at complexity 0.</summary>
    internal static bool TryGet(OpusBandwidth bandwidth, int milliseconds, int bitrate, out double db) =>
        Table.TryGetValue((bandwidth, milliseconds, bitrate), out db) && !double.IsNaN(db);
}

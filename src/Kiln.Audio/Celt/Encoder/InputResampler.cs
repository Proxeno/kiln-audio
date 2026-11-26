// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

namespace Kiln.Audio.Celt.Encoder;

/// <summary>
/// Maps an API sample rate onto the 48 kHz CELT mode of RFC 6716 §4.3.
/// </summary>
/// <remarks>
/// <para>
/// CELT always runs internally at 48 kHz. For API rates below 48 kHz this implementation uses the
/// reference's original upsampler: input samples are zero-stuffed into the 48 kHz stream (see
/// <see cref="CeltEncoderKernels.PreEmphasis"/>), the MDCT bins above the input Nyquist rate are
/// cleared, and the coded band count is reduced to the end band the RFC allows for the rate
/// (§4.3, and the end-band rule used by Appendix A <c>celt.c</c>). The end band is signalled
/// through the TOC configuration, so no extra side information is needed.
/// </para>
/// <para>
/// The upsampler is non-normative. It has no resampling filter, so it adds the same
/// <c>(upsample - 1) / 48000</c> seconds of internal delay the reference has and leaves images
/// above the input Nyquist rate unreconstructed; those bins are discarded before coding. The
/// CELT stream delay itself (2.5 ms of overlap) is unchanged.
/// </para>
/// </remarks>
internal static class InputResampler
{
    /// <summary>The internal CELT sample rate.</summary>
    internal const int InternalSampleRate = 48000;

    /// <summary>
    /// Returns the number of 48 kHz samples produced for one API sample: 8000→6, 12000→4,
    /// 16000→3, 24000→2, 48000→1 (RFC 6716 Appendix A, <c>celt.c</c>: <c>resampling_factor</c>).
    /// </summary>
    /// <param name="sampleRate">The validated API sample rate.</param>
    /// <returns>The upsample factor.</returns>
    internal static int UpsampleFactor(int sampleRate) => sampleRate switch
    {
        48000 => 1,
        24000 => 2,
        16000 => 3,
        12000 => 4,
        8000 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Unsupported sample rate."),
    };

    /// <summary>
    /// Returns the first uncoded band for an API rate (RFC 6716 §4.3; Appendix A,
    /// <c>opus_encoder.c</c> end-band selection).
    /// </summary>
    /// <param name="sampleRate">The validated API sample rate.</param>
    /// <returns>
    /// 21 for 48 kHz (fullband), 19 for 24 kHz (super-wideband), 17 for 16 kHz and 12 kHz
    /// (wideband), and 13 for 8 kHz (narrowband). Twelve kilohertz maps to wideband because
    /// CELT-only packets cannot signal mediumband.
    /// </returns>
    internal static int EndBand(int sampleRate) => sampleRate switch
    {
        48000 => 21,
        24000 => 19,
        16000 => 17,
        12000 => 17,
        8000 => 13,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Unsupported sample rate."),
    };

    /// <summary>
    /// Returns the TOC configuration base for the API rate: the first CELT-only configuration of
    /// its bandwidth group (RFC 6716 §3.1, Table 2). The frame-duration index is added to this.
    /// </summary>
    /// <param name="sampleRate">The validated API sample rate.</param>
    /// <returns>16 (narrowband), 20 (wideband), 24 (super-wideband) or 28 (fullband).</returns>
    internal static int TocConfigBase(int sampleRate) => sampleRate switch
    {
        8000 => 16,
        12000 => 20,
        16000 => 20,
        24000 => 24,
        48000 => 28,
        _ => throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Unsupported sample rate."),
    };
}
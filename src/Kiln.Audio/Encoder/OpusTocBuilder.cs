// Portions derived from the RFC 6716 reference implementation (Appendix A), (c) IETF Trust and the persons identified as authors, Simplified BSD License; see THIRD-PARTY-NOTICES.md.

namespace Kiln.Audio.Encoder;

/// <summary>
/// Builds the table-of-contents byte of RFC 6716 §3.1 from a coding mode, bandwidth, frame
/// duration and channel count.
/// </summary>
/// <remarks>
/// This is the encoder-side inverse of <see cref="OpusToc.Parse"/>, transcribed from the
/// reference <c>gen_toc()</c> (RFC 6716 Appendix A, <c>src/opus_encoder.c</c>). CELT has no
/// mediumband configuration, so a mediumband request is signalled as wideband.
/// </remarks>
internal static class OpusTocBuilder
{
    /// <summary>Builds a TOC byte (RFC 6716 §3.1, Table 2).</summary>
    /// <param name="mode">The coding mode.</param>
    /// <param name="bandwidth">The audio bandwidth.</param>
    /// <param name="duration">The frame duration.</param>
    /// <param name="stereo">True to signal a stereo stream.</param>
    /// <returns>The TOC byte; the frame-count code is 0.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The mode, bandwidth and duration do not name a configuration in Table 2.
    /// </exception>
    internal static byte Build(OpusMode mode, OpusBandwidth bandwidth, OpusFrameDuration duration, bool stereo)
    {
        int config = ConfigOf(mode, bandwidth, duration);
        return (byte)((config << 3) | (stereo ? 0x04 : 0));
    }

    /// <summary>Returns the Table 2 configuration number for a mode, bandwidth and duration.</summary>
    /// <param name="mode">The coding mode.</param>
    /// <param name="bandwidth">The audio bandwidth.</param>
    /// <param name="duration">The frame duration.</param>
    /// <returns>The configuration number, 0 to 31.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The mode, bandwidth and duration do not name a configuration in Table 2.
    /// </exception>
    internal static int ConfigOf(OpusMode mode, OpusBandwidth bandwidth, OpusFrameDuration duration)
    {
        switch (mode)
        {
            case OpusMode.Silk:
                // Configs 0-11: SILK-only at NB, MB and WB, with 10, 20, 40 and 60 ms frames.
                if (bandwidth is not (OpusBandwidth.Narrowband or OpusBandwidth.Mediumband or OpusBandwidth.Wideband)
                    || duration is not (OpusFrameDuration.Ms10 or OpusFrameDuration.Ms20
                        or OpusFrameDuration.Ms40 or OpusFrameDuration.Ms60))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(duration), duration, "SILK-only frames are 10 to 60 ms at narrowband, mediumband or wideband.");
                }

                return ((int)bandwidth * 4) + ((int)duration - (int)OpusFrameDuration.Ms10);

            case OpusMode.Hybrid:
                // Configs 12-15: hybrid at SWB and FB, with 10 and 20 ms frames.
                if (bandwidth is not (OpusBandwidth.SuperWideband or OpusBandwidth.Fullband)
                    || duration is not (OpusFrameDuration.Ms10 or OpusFrameDuration.Ms20))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(duration), duration, "Hybrid frames are 10 or 20 ms at super-wideband or fullband.");
                }

                return 12
                    + (bandwidth == OpusBandwidth.Fullband ? 2 : 0)
                    + (duration == OpusFrameDuration.Ms20 ? 1 : 0);

            case OpusMode.Celt:
                // Configs 16-31: CELT-only at NB, WB, SWB and FB, with 2.5, 5, 10 and 20 ms frames.
                if (duration is not (OpusFrameDuration.Ms2_5 or OpusFrameDuration.Ms5
                        or OpusFrameDuration.Ms10 or OpusFrameDuration.Ms20))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(duration), duration, "CELT-only frames are 2.5, 5, 10 or 20 ms.");
                }

                int group = bandwidth switch
                {
                    OpusBandwidth.Narrowband => 0,
                    OpusBandwidth.Mediumband or OpusBandwidth.Wideband => 1,
                    OpusBandwidth.SuperWideband => 2,
                    OpusBandwidth.Fullband => 3,
                    _ => throw new ArgumentOutOfRangeException(nameof(bandwidth), bandwidth, "Unknown bandwidth."),
                };
                return 16 + (group * 4) + (int)duration;

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown coding mode.");
        }
    }
}
